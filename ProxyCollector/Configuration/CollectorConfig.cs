using System;
using System.Linq; // 用于 .Any() 和 .ToArray()
using System.Collections.Generic; // 用于 List<T>

namespace ProxyCollector.Configuration;

/// <summary>
/// 提供应用程序配置的单例实例，该配置从环境变量加载。
/// 此配置包含 GitHub 凭据、Sing-box 路径、输出路径、
/// 并发设置、超时以及代理订阅源。
/// </summary>
public class CollectorConfig
{
    /// <summary>
    /// 获取 CollectorConfig 的单例实例。
    /// </summary>
    public static CollectorConfig Instance { get; private set; }

    /// <summary>
    /// 获取 GitHub API 令牌。这是一个必需的设置。
    /// </summary>
    public required string GithubApiToken { get; init; }

    /// <summary>
    /// 获取拥有仓库的 GitHub 用户名或组织。这是一个必需的设置。
    /// </summary>
    public required string GithubUser { get; init; }

    /// <summary>
    /// 获取 GitHub 仓库名称。这是一个必需的设置。
    /// </summary>
    public required string GithubRepo { get; init; }

    /// <summary>
    /// 获取用于提交结果的 GitHub 分支。默认为 "main"。
    /// </summary>
    public string GithubBranch { get; init; } = "main"; // 新增属性，带默认值

    /// <summary>
    /// 获取 Sing-box 可执行文件的文件路径。这是一个必需的设置。
    /// </summary>
    public required string SingboxPath { get; init; }

    /// <summary>
    /// 获取 GitHub 仓库中 V2Ray 格式结果的文件路径。这是一个必需的设置。
    /// </summary>
    public required string V2rayFormatResultPath { get; init; }

    /// <summary>
    /// 获取 GitHub 仓库中 Sing-box 格式结果的文件路径。这是一个必需的设置。
    /// </summary>
    public required string SingboxFormatResultPath { get; init; }

    /// <summary>
    /// 获取用于获取或测试等操作的最大并发线程/任务数。
    /// 如果未指定或无效，则默认为 5。
    /// </summary>
    public int MaxThreadCount { get; init; }

    /// <summary>
    /// 获取网络操作和代理测试的超时时间，以毫秒为单位。
    /// 如果未指定或无效，则默认为 20000 毫秒（20 秒）。
    /// </summary>
    public TimeSpan Timeout { get; init; } // 类型从 int 更改为 TimeSpan

    /// <summary>
    /// 获取下载订阅内容的超时时间，以秒为单位。
    /// 如果未指定或无效，则默认为 8 秒。
    /// </summary>
    public TimeSpan DownloadTimeoutSeconds { get; init; } // 新增属性

    /// <summary>
    /// 获取用于连接测试的端口（例如，用于 Sing-box）。
    /// 如果未指定或无效，则默认为 20000。
    /// </summary>
    public int ConnectionTestPort { get; init; } // 新增属性

    /// <summary>
    /// 获取 Sing-box 操作的缓冲区大小。
    /// 如果未指定或无效，则默认为 1024。
    /// </summary>
    public int BufferSize { get; init; } // 新增属性

    /// <summary>
    /// 获取用于收集代理订阅的 URL 数组。这是一个必需的设置。
    /// </summary>
    public required string[] Sources { get; init; }

    /// <summary>
    /// 静态构造函数，用于初始化 CollectorConfig 的单例实例。
    /// 这确保了当类首次被访问时，配置只被加载一次。
    /// </summary>
    static CollectorConfig()
    {
        Instance = CreateInstance();
    }

    /// <summary>
    /// 私有构造函数，用于阻止直接实例化并强制执行单例模式。
    /// </summary>
    private CollectorConfig() { }

    /// <summary>
    /// 从环境变量创建并填充一个新的 CollectorConfig 实例。
    /// 包括带有默认回退和错误日志记录的健壮解析。
    /// </summary>
    /// <returns>一个新的 CollectorConfig 实例。</returns>
    /// <exception cref="InvalidOperationException">如果必需的环境变量缺失，则抛出此异常。</exception>
    private static CollectorConfig CreateInstance()
    {
        // 辅助方法，用于获取必需的环境变量，如果缺失则抛出异常
        string GetRequiredEnv(string varName)
        {
            return Environment.GetEnvironmentVariable(varName) ??
                   throw new InvalidOperationException($"环境变量 '{varName}' 未设置。这是一个必需的配置。");
        }

        // 辅助方法，用于解析 int 类型环境变量，如果缺失或无效则使用默认值
        int ParseEnvOrDefault(string varName, int defaultValue)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(varName), out int value))
            {
                return value;
            }
            Console.WriteLine($"警告: 环境变量 '{varName}' 缺失或无效。使用默认值: {defaultValue}");
            return defaultValue;
        }

        // 辅助方法，用于从秒数解析 TimeSpan 类型环境变量，如果缺失或无效则使用默认时间
        TimeSpan ParseTimeSpanEnvOrDefault(string varName, int defaultSeconds)
        {
            if (int.TryParse(Environment.GetEnvironmentVariable(varName), out int seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
            Console.WriteLine($"警告: 环境变量 '{varName}' 缺失或无效。使用默认时间 (秒): {defaultSeconds}");
            return TimeSpan.FromSeconds(defaultSeconds);
        }

        // 辅助方法，用于解析包含多个分隔符的字符串数组
        string[] ParseSourcesEnv(string varName)
        {
            string? sourcesString = Environment.GetEnvironmentVariable(varName);
            if (string.IsNullOrWhiteSpace(sourcesString))
            {
                throw new InvalidOperationException($"环境变量 '{varName}' 未设置或为空。至少需要一个源 URL。");
            }
            // 使用换行符、逗号或分号分隔，并移除空条目
            var sources = sourcesString.Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(s => s.Trim()) // 移除每个条目首尾空格
                                       .Where(s => !string.IsNullOrWhiteSpace(s)) // 过滤掉空字符串
                                       .ToArray();
            if (!sources.Any())
            {
                throw new InvalidOperationException($"环境变量 '{varName}' 解析后不包含任何有效的源 URL。");
            }
            return sources;
        }

        return new CollectorConfig
        {
            GithubApiToken = GetRequiredEnv("GithubApiToken"),
            GithubUser = GetRequiredEnv("GithubUser"),
            GithubRepo = GetRequiredEnv("GithubRepo"),
            GithubBranch = Environment.GetEnvironmentVariable("GithubBranch") ?? "main", // 可选，带默认值
            V2rayFormatResultPath = GetRequiredEnv("V2rayFormatResultPath"),
            SingboxFormatResultPath = GetRequiredEnv("SingboxFormatResultPath"),
            SingboxPath = GetRequiredEnv("SingboxPath"),
            MaxThreadCount = ParseEnvOrDefault("MaxThreadCount", 5), // 默认为 5 个线程
            Timeout = TimeSpan.FromMilliseconds(ParseEnvOrDefault("TimeoutMs", 20000)), // 默认为 20 秒，假设原始 "Timeout" 是毫秒
            DownloadTimeoutSeconds = ParseTimeSpanEnvOrDefault("DownloadTimeoutSeconds", 8), // 新增属性，默认为 8 秒
            ConnectionTestPort = ParseEnvOrDefault("ConnectionTestPort", 20000), // 新增属性，默认为 20000
            BufferSize = ParseEnvOrDefault("BufferSize", 1024), // 新增属性，默认为 1024
            Sources = ParseSourcesEnv("Sources") // 使用改进的解析方法
        };
    }
}
