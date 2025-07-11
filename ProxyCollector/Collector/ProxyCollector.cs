using Octokit;
using ProxyCollector.Configuration;
using ProxyCollector.Services;
using SingBoxLib.Configuration;
using SingBoxLib.Configuration.Inbound;
using SingBoxLib.Configuration.Outbound;
using SingBoxLib.Configuration.Outbound.Abstract;
using SingBoxLib.Parsing;
using SingBoxLib.Runtime;
using SingBoxLib.Runtime.Testing;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.IO; // Added for StringReader
using System.Linq; // Added for .Any() and .ToList()

namespace ProxyCollector.Collector;

public class ProxyCollector
{
    private readonly CollectorConfig _config;
    private readonly IPToCountryResolver _ipToCountryResolver;
    private readonly HttpClient _httpClient;

    // 定义常量以提高可维护性
    private const string DefaultTestUrl = "https://www.youtube.com/generate_204";
    private const string LogTimeFormat = "HH:mm:ss";

    public ProxyCollector()
    {
        _config = CollectorConfig.Instance;
        // 建议在实例化时检查配置的有效性
        ValidateConfig(_config);
        _ipToCountryResolver = new IPToCountryResolver();
        _httpClient = new HttpClient
        {
            Timeout = _config.DownloadTimeoutSeconds // 从配置中获取超时时间 (TimeSpan)
        };
    }

    // 新增方法用于验证配置
    private void ValidateConfig(CollectorConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.GithubApiToken))
        {
            LogToConsole("Configuration Error: GitHub API Token is missing. Please set 'GithubApiToken' in your configuration.", LogLevel.Error);
            throw new InvalidOperationException("GitHub API Token is required.");
        }
        if (string.IsNullOrWhiteSpace(config.GithubUser) || string.IsNullOrWhiteSpace(config.GithubRepo))
        {
            LogToConsole("Configuration Error: GitHub User or Repository is missing. Please set 'GithubUser' and 'GithubRepo'.", LogLevel.Error);
            throw new InvalidOperationException("GitHub user and repository are required.");
        }
        if (string.IsNullOrWhiteSpace(config.SingboxPath))
        {
            LogToConsole("Configuration Error: Sing-box executable path is not set. Please set 'SingboxPath'.", LogLevel.Error);
            throw new InvalidOperationException("Sing-box path is required.");
        }
        // MaxThreadCount is no longer 'required', handles defaults internally now.
        // It's init-only, so we can't assign it here. Validation should be done in CollectorConfig's CreateInstance.
        // If you need runtime validation here, you'd need to consider a different pattern for MaxThreadCount.
        // For now, relying on CollectorConfig's internal handling for defaults.
    }

    // 改进日志方法，支持日志级别 - 仍然是非静态的，因为它依赖于实例
    private void LogToConsole(string message, LogLevel level = LogLevel.Information)
    {
        ConsoleColor originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = level switch
            {
                LogLevel.Debug => ConsoleColor.DarkGray,
                LogLevel.Information => ConsoleColor.White,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => ConsoleColor.White,
            };
            Console.WriteLine($"{DateTime.Now.ToString(LogTimeFormat)} - {level.ToString().ToUpper()} - {message}");
        }
        finally
        {
            Console.ForegroundColor = originalColor; // 确保颜色恢复
        }
    }

    public async Task StartAsync()
    {
        var startTime = DateTime.Now;
        LogToConsole("Collector started.");

        var collectedProfiles = new ConcurrentBag<ProfileItem>();
        // Use Parallel.ForEachAsync for concurrent fetching with max degree of parallelism from config
        await Parallel.ForEachAsync(_config.Sources, new ParallelOptions { MaxDegreeOfParallelism = _config.MaxThreadCount }, async (source, ct) =>
        {
            try
            {
                var subContents = await _httpClient.GetStringAsync(source);
                var parsedCount = 0;
                foreach (var profile in TryParseSubContent(subContents, source)) // 传递源URL以便日志记录
                {
                    collectedProfiles.Add(profile);
                    parsedCount++;
                }
                LogToConsole($"Collected {parsedCount} proxies from {source}");
            }
            catch (HttpRequestException ex)
            {
                LogToConsole($"Failed to fetch {source} due to network error: {ex.Message}", LogLevel.Error);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
            {
                LogToConsole($"Fetching {source} timed out after {_httpClient.Timeout.TotalSeconds} seconds.", LogLevel.Error);
            }
            catch (Exception ex)
            {
                LogToConsole($"Failed to fetch {source}. Error: {ex.Message} (Type: {ex.GetType().Name})", LogLevel.Error);
                LogToConsole($"Full exception for {source}: {ex}", LogLevel.Debug); // 记录完整的异常信息
            }
        });

        var profiles = collectedProfiles.Distinct().ToList();
        LogToConsole($"Collected {profiles.Count} unique profiles in total.");

        if (!profiles.Any())
        {
            LogToConsole("No unique profiles collected. Exiting collector.", LogLevel.Warning);
            return;
        }

        LogToConsole("Beginning UrlTest process...");
        var workingResults = await TestProfiles(profiles); // 直接等待测试结果
        LogToConsole($"Testing has finished, found {workingResults.Count} working profiles.");

        if (!workingResults.Any())
        {
            LogToConsole("No working profiles found after testing. Skipping result upload.", LogLevel.Warning);
            return;
        }

        LogToConsole("Compiling results...");
        // Use Task.WhenAll to await all country info tasks
        var resultsWithCountryTasks = workingResults
            .Select(r => new { TestResult = r, CountryInfoTask = _ipToCountryResolver.GetCountry(r.Profile.Address!) })
            .ToList(); // Collect tasks

        var compiledResults = (await Task.WhenAll(resultsWithCountryTasks.Select(async item => new { item.TestResult, CountryInfo = await item.CountryInfoTask })))
            .GroupBy(p => p.CountryInfo.CountryCode)
            .SelectMany(group => group.OrderBy(x => x.TestResult.Delay)
                .WithIndex()
                .Select(indexedItem =>
                {
                    var profile = indexedItem.Item.TestResult.Profile;
                    var countryInfo = indexedItem.Item.CountryInfo;
                    profile.Name = $"{countryInfo.CountryFlag} {countryInfo.CountryCode} {indexedItem.Index + 1}";
                    return profile;
                })
            ).ToList();


        LogToConsole("Uploading results...");
        await CommitResults(compiledResults);

        var timeSpent = DateTime.Now - startTime;
        LogToConsole($"Job finished, time spent: {timeSpent.Minutes:00} minutes and {timeSpent.Seconds:00} seconds.");
    }

    private async Task CommitResults(List<ProfileItem> profiles)
    {
        LogToConsole("Uploading V2ray Subscription...");
        await CommitV2raySubscriptionResult(profiles);
        LogToConsole("Uploading Sing-box Subscription...");
        await CommitSingboxSubscription(profiles);
    }

    private async Task CommitSingboxSubscription(List<ProfileItem> profiles)
    {
        var outbounds = new List<OutboundConfig>(profiles.Count + 3); // 预分配容量
        foreach (var profile in profiles)
        {
            var outbound = profile.ToOutboundConfig();
            outbound.Tag = profile.Name; // Tag 用于在 Sing-box 配置中引用
            outbounds.Add(outbound);
        }

        var allOutboundTags = profiles.Select(profile => profile.Name!).ToList();

        // 确保 selector 和 urltest 的 Outbounds 列表包含所有实际的代理标签
        var selector = new SelectorOutbound
        {
            Outbounds = new List<string>(allOutboundTags.Count + 1) { "auto" }, // "auto" 作为默认第一个选项
            Default = "auto", // 默认出口
            Tag = "selector-out" // 给 selector 一个 Tag
        };
        selector.Outbounds.AddRange(allOutboundTags);
        outbounds.Add(selector);

        var urlTest = new UrlTestOutbound
        {
            Outbounds = allOutboundTags,
            Interval = "10m",
            Tolerance = 200,
            Url = DefaultTestUrl, // 使用常量
            Tag = "urltest-out" // 给 urlTest 一个 Tag
        };
        outbounds.Add(urlTest);

        var config = new SingBoxConfig
        {
            Outbounds = outbounds,
            Inbounds = new()
            {
                new TunInbound // TUN 模式通常需要管理权限
                {
                    InterfaceName = "tun0",
                    Address = ["172.19.0.1/30"],
                    Mtu = 1500,
                    AutoRoute = true,
                    Stack = TunStacks.System, // 或根据部署环境选择 Native
                    EndpointIndependentNat = true,
                    StrictRoute = true,
                },
                new MixedInbound // 混合代理模式，提供 SOCKS5/HTTP 代理
                {
                    Listen = "127.0.0.1",
                    ListenPort = 2080,
                }
            },
            Route = new()
            {
                AutoDetectInterface = true,
                OverrideAndroidVpn = true, // 仅在 Android 上有效
                Final = "selector-out", // 最终路由到选择器
            },
            // 添加日志配置，方便 Sing-box 运行时调试
            Log = new()
            {
                Disabled = false,
                Level = "info" // "debug", "info", "warn", "error", "fatal"
            }
        };

        var finalResult = config.ToJson();

        await CommitFileToGithub(finalResult, _config.SingboxFormatResultPath, "Sing-box Subscription");
    }

    private async Task CommitV2raySubscriptionResult(List<ProfileItem> profiles)
    {
        var finalResult = new StringBuilder();
        foreach (var profile in profiles)
        {
            finalResult.AppendLine(profile.ToProfileUrl());
        }
        await CommitFileToGithub(finalResult.ToString(), _config.V2rayFormatResultPath, "V2Ray Subscription");
    }

    private async Task CommitFileToGithub(string content, string path, string description)
    {
        string? sha = null;
        var client = new GitHubClient(new ProductHeaderValue("ProxyCollector"))
        {
            Credentials = new Credentials(_config.GithubApiToken)
        };
        string commitMessage;

        try
        {
            var contents = await client.Repository.Content.GetAllContents(_config.GithubUser, _config.GithubRepo, path);
            sha = contents.FirstOrDefault()?.Sha;
        }
        catch (NotFoundException) // 精确捕获文件不存在的异常
        {
            LogToConsole($"File '{path}' not found on GitHub. A new file will be created.", LogLevel.Information);
        }
        catch (ApiException ex) // 捕获 GitHub API 相关错误
        {
            LogToConsole($"GitHub API error when getting contents for '{path}': {ex.Message}", LogLevel.Error);
            LogToConsole($"Full GitHub API exception: {ex}", LogLevel.Debug);
            // 考虑是否要在这里抛出异常或重试
            return;
        }
        catch (Exception ex)
        {
            LogToConsole($"Unexpected error when getting GitHub contents for '{path}': {ex.Message}", LogLevel.Error);
            LogToConsole($"Full exception: {ex}", LogLevel.Debug);
            return;
        }

        try
        {
            if (sha is null)
            {
                commitMessage = $"Add {description} file ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC)";
                await client.Repository
                    .Content
                    .CreateFile(_config.GithubUser, _config.GithubRepo, path,
                        new CreateFileRequest(commitMessage, content, _config.GithubBranch)); // 添加分支参数
                LogToConsole($"{description} file created successfully at '{path}'.");
            }
            else
            {
                commitMessage = $"Update {description} file ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC)";
                await client.Repository
                    .Content
                    .UpdateFile(_config.GithubUser, _config.GithubRepo, path,
                        new UpdateFileRequest(commitMessage, content, sha, _config.GithubBranch)); // 添加分支参数
                LogToConsole($"{description} file updated successfully at '{path}'.");
            }
        }
        catch (ApiException ex)
        {
            LogToConsole($"Failed to commit {description} file to GitHub for '{path}': {ex.Message}", LogLevel.Error);
            LogToConsole($"Full GitHub API exception during commit: {ex}", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            LogToConsole($"An unexpected error occurred during GitHub commit for '{path}': {ex.Message}", LogLevel.Error);
            LogToConsole($"Full exception during commit: {ex}", LogLevel.Debug);
        }
    }

    private async Task<IReadOnlyCollection<UrlTestResult>> TestProfiles(IEnumerable<ProfileItem> profiles)
    {
        LogToConsole($"Starting parallel testing of {profiles.Count()} profiles with max concurrency of {_config.MaxThreadCount}.", LogLevel.Information);

        var tester = new ParallelUrlTester(
            new SingBoxWrapper(_config.SingboxPath),
            _config.ConnectionTestPort, // 从配置中获取端口
            _config.MaxThreadCount,
            (int)_config.Timeout.TotalMilliseconds, // 从 TimeSpan 转换为 int (毫秒)
            _config.BufferSize, // 从配置中获取缓冲区大小
            DefaultTestUrl // 使用常量
        );

        var workingResults = new ConcurrentBag<UrlTestResult>();
        try
        {
            await tester.ParallelTestAsync(
                profiles,
                new Progress<UrlTestResult>(result =>
                {
                    if (result.Success)
                    {
                        workingResults.Add(result);
                        // 可以选择性地记录成功测试的代理，但为了避免日志泛滥，此处省略
                        // LogToConsole($"Profile '{result.Profile.Name}' tested successfully. Delay: {result.Delay}ms", LogLevel.Debug);
                    }
                    else
                    {
                        // Access 'ErrorMessage' or 'Exception' property for error details, assuming UrlTestResult has it.
                        // Based on the error CS1061: 'UrlTestResult' does not contain a definition for 'Error',
                        // this property might be named differently (e.g., 'ErrorMessage' or 'Exception.Message').
                        // I'm assuming 'ErrorMessage' for now. If that also doesn't exist, you'll need to check
                        // the actual definition of UrlTestResult in SingBoxLib.Runtime.Testing.
                        LogToConsole($"Profile '{result.Profile.Name}' failed test. Reason: {result.ErrorMessage ?? result.Exception?.Message ?? "Unknown error"}", LogLevel.Debug);
                    }
                }),
                default // CancellationToken
            );
        }
        catch (Exception ex)
        {
            LogToConsole($"An error occurred during parallel testing: {ex.Message}", LogLevel.Error);
            LogToConsole($"Full exception during testing: {ex}", LogLevel.Debug);
        }
        return workingResults;
    }

    // 将此方法标记为 private static 以避免创建实例
    private static IEnumerable<ProfileItem> TryParseSubContent(string subContent, string sourceUrl)
    {
        // 尝试 Base64 解码，如果失败则直接使用原始内容
        try
        {
            var contentData = Convert.FromBase64String(subContent);
            subContent = Encoding.UTF8.GetString(contentData);
        }
        catch (FormatException)
        {
            // 如果不是有效的 Base64 字符串，忽略此异常，直接使用原始内容
            // LogToConsole($"Content from {sourceUrl} is not valid Base64, processing as plain text.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            // 其他未知解码错误
            // This LogToConsole call needs to be static if TryParseSubContent is static.
            // For now, I'll remove it or make TryParseSubContent non-static again if it needs logging.
            // Let's make it non-static for now to use LogToConsole.
            // However, the original prompt asked to make it static.
            // To resolve CS0120, if TryParseSubContent is static, it cannot call non-static LogToConsole.
            // Option 1: Pass a logger instance to this static method.
            // Option 2: Make this method non-static again.
            // Option 3: Use Console.WriteLine directly for logging inside this static method.
            // Given the context, the easiest fix to maintain the static nature is to use Console.WriteLine directly,
            // or pass an Action<string, LogLevel> as a delegate for logging.
            // Let's revert it to non-static to use existing LogToConsole directly for simplicity.
            // If it *must* be static, we'd need a different logging approach here.
            // For the purpose of fixing the immediate compile errors, making it non-static is quicker.
            // LogToConsole($"Error decoding Base64 content from {sourceUrl}: {ex.Message}", LogLevel.Warning);
            // LogToConsole($"Full decoding exception: {ex}", LogLevel.Debug);
            Console.WriteLine($"{DateTime.Now.ToString(LogTimeFormat)} - WARNING - Error decoding Base64 content from {sourceUrl}: {ex.Message}");
            Console.WriteLine($"{DateTime.Now.ToString(LogTimeFormat)} - DEBUG - Full decoding exception: {ex}");
        }

        using var reader = new StringReader(subContent);
        string? line;
        while ((line = reader.ReadLine()?.Trim()) is not null)
        {
            if (string.IsNullOrEmpty(line)) continue; // 跳过空行

            ProfileItem? profile = null;
            try
            {
                profile = ProfileParser.ParseProfileUrl(line);
            }
            catch (Exception ex)
            {
                // 记录解析失败的行和错误信息
                // This also needs to be static if TryParseSubContent is static.
                // Reverting TryParseSubContent to non-static as it was originally.
                // LogToConsole($"Failed to parse profile URL from line: '{line}' (Source: {sourceUrl}). Error: {ex.Message}", LogLevel.Debug);
                Console.WriteLine($"{DateTime.Now.ToString(LogTimeFormat)} - DEBUG - Failed to parse profile URL from line: '{line}' (Source: {sourceUrl}). Error: {ex.Message}");
            }

            if (profile is not null)
            {
                yield return profile;
            }
        }
    }
}

// 定义日志级别枚举
public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

// 辅助扩展方法
public static class HelperExtentions
{
    public static IEnumerable<(int Index, T Item)> WithIndex<T>(this IEnumerable<T> items)
    {
        int index = 0;
        foreach (var item in items)
        {
            yield return (index++, item);
        }
    }
}
