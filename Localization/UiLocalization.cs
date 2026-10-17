using System.Linq;
using AIWeather.Models;
using AIWeather.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace AIWeather.Localization
{
    /// <summary>
    /// Small, dependency-free UI localizer. N.I.N.A. sets CurrentUICulture from the
    /// active profile before plugins are composed, so the plugin follows the same
    /// language without storing a second language preference. English is the fallback
    /// for every non-Chinese culture.
    /// </summary>
    public static class UiLocalization
    {
        private readonly record struct Translation(string English, string Chinese);

        private static readonly IReadOnlyDictionary<string, Translation> Strings =
            new Dictionary<string, Translation>(StringComparer.Ordinal)
            {
                ["Common.Browse"] = new("Browse...", "浏览…"),
                ["Common.Open"] = new("Open", "打开"),
                ["Common.Try"] = new("Try", "测试"),
                ["Common.Refresh"] = new("Refresh", "刷新"),
                ["Common.Choose"] = new("Choose...", "选择…"),
                ["Common.MoveUp"] = new("Move up", "上移"),
                ["Common.MoveDown"] = new("Move down", "下移"),
                ["Common.Reset"] = new("Reset", "恢复默认"),
                ["Common.Unknown"] = new("Unknown", "未知"),
                ["Common.Invalid"] = new("invalid", "无效"),
                ["Common.True"] = new("yes", "是"),
                ["Common.False"] = new("no", "否"),

                ["Preview.Title"] = new("AI Weather Monitor", "AI 天气监视器"),
                ["Preview.Safe"] = new("SAFE", "安全"),
                ["Preview.Unsafe"] = new("UNSAFE", "不安全"),
                ["Preview.Cloud"] = new("Cloud: ", "云量："),
                ["Preview.Confidence"] = new(" | Confidence: ", "｜置信度："),
                ["Preview.Limits"] = new("Limits (High/Low): ", "阈值（高/低）："),
                ["Preview.KeepFrame"] = new("Keep current frame", "保留当前帧"),
                ["Preview.ReviewLabels"] = new("Review labels…", "检查标签…"),
                ["Preview.ActivityLog"] = new("Activity Log", "活动日志"),
                ["Preview.LastUpdate"] = new("Last update: ", "最后更新："),
                ["Preview.CameraSources"] = new("Camera sources", "相机源"),
                ["Preview.Username"] = new("Username", "用户名"),
                ["Preview.Password"] = new("Password", "密码"),
                ["Preview.Protocol"] = new("Protocol", "协议"),
                ["Preview.MediaUrl"] = new("Media URL", "媒体 URL"),
                ["Preview.ImageUrl"] = new("Image URL", "图像 URL"),
                ["Preview.FolderPath"] = new("Folder Path", "文件夹路径"),
                ["Preview.AddSource"] = new("Add new camera source", "添加相机源"),
                ["Preview.DeleteSource"] = new("Delete selected source", "删除所选相机源"),
                ["Preview.ReplicaTitle"] = new("Primary weather connection", "主节点天气连接"),
                ["Preview.ReplicaNotStarted"] = new("Synchronization has not started. Connect here, or connect AI Weather from N.I.N.A.'s Safety Monitor equipment page.", "同步尚未启动。请在此连接，或在 N.I.N.A. 的安全监视器设备页连接 AI Weather。"),
                ["Preview.ReplicaNoVideo"] = new("This replica keeps one local camera stream for preview. While the primary is healthy it does not run local weather analysis; if takeover activates, AI reuses the same preview frames instead of opening a second stream.", "本从节点始终保持一条本地相机预览流。主节点健康时不执行本地天气分析；触发接管后，AI 会复用同一预览帧，不会再打开第二路视频流。"),
                ["Preview.ReplicaFailover"] = new("The primary is unavailable and this replica has temporarily taken over camera capture and analysis. It continues probing the primary and will return only after the recovery period.", "主节点不可达，本从节点已临时接管相机采集与天气分析。它仍会持续探测主节点，并只在恢复稳定期结束后回切。"),
                ["Preview.ConnectPrimary"] = new("Connect primary", "连接主节点"),
                ["Preview.DisconnectPrimary"] = new("Disconnect primary", "断开主节点"),
                ["Preview.ReplicaConnectHelp"] = new("Start or stop authenticated weather-status synchronization with the configured primary node", "启动或停止与已配置主节点之间经过认证的天气状态同步"),
                ["Preview.StopReplica"] = new("Stop replica monitoring", "停止从节点监控"),
                ["Preview.StopReplicaHelp"] = new("Stop synchronization, local takeover analysis, and the local preview on this replica only; the primary node is not affected", "仅停止本从节点的同步、本地接管分析和本地预览；不会影响主节点"),
                ["Preview.VideoConnecting"] = new("Connecting to the local camera stream…", "正在连接本机相机视频流……"),
                ["Preview.VideoLibVlcUnavailable"] = new("The local video engine could not be initialized.", "本机视频引擎初始化失败。"),
                ["Preview.VideoViewUnavailable"] = new("The video panel is not ready.", "视频面板尚未就绪。"),
                ["Preview.VideoSurfaceWaiting"] = new("The camera stream is connected; waiting for the first displayable frame while same-session recovery continues…", "相机视频流已连接；正在等待首个可显示画面，并继续使用同一连接恢复……"),
                ["Preview.VideoSurfaceUnavailable"] = new("The camera is decoding, but the local video surface could not be displayed.", "相机视频正在解码，但本机画面无法显示。"),
                ["Preview.VideoOpenFailed"] = new("The local camera stream could not be opened. Check this computer's N.I.N.A. log for details.", "本机无法打开相机视频流，请查看这台电脑的 N.I.N.A. 日志了解详情。"),
                ["Preview.VideoUrlInvalid"] = new("The synchronized camera URL is invalid.", "同步得到的相机地址格式无效。"),
                ["Preview.VideoRetry"] = new("Retry local video", "重试本机视频"),
                ["Equipment.Category"] = new("All Sky Camera", "全天相机"),
                ["Equipment.Name"] = new("All Sky Camera Safety Monitor", "全天相机安全监视器"),
                ["Equipment.Description"] = new("Monitors all-sky camera weather and provides an imaging safety status", "监视全天相机天气，并提供拍摄安全状态"),
                ["Equipment.DriverInfo"] = new("All Sky Camera Plugin", "全天相机插件"),

                ["Options.Cluster"] = new("LAN primary / replica", "局域网主节点 / 从节点"),
                ["Options.ClusterMode"] = new("Node mode:", "节点模式："),
                ["Options.ClusterStandalone"] = new("Standalone (this computer captures and analyzes)", "独立模式（本机采集并分析）"),
                ["Options.ClusterPrimary"] = new("Primary (capture once and publish status)", "主节点（采集一次并发布状态）"),
                ["Options.ClusterReplica"] = new("Replica (primary preferred; optional local failover)", "从节点（优先主节点；可选本地接管）"),
                ["Options.ClusterModeHelp"] = new("One package contains all roles. Changing the role takes effect after the Safety Monitor is disconnected and reconnected. There is no automatic leader election.", "同一个插件包包含全部角色。角色变更会在安全监视器断开并重新连接后生效；系统不会自动选主。"),
                ["Options.ClusterListenPort"] = new("Primary listen port:", "主节点监听端口："),
                ["Options.ClusterPrimaryUrl"] = new("Primary node URL:", "主节点地址："),
                ["Options.ClusterToken"] = new("Shared token (at least 16 characters):", "共享令牌（至少 16 个字符）："),
                ["Options.ClusterGenerateToken"] = new("Generate", "生成"),
                ["Options.ClusterGenerateTokenHelp"] = new("Generate a 256-bit cryptographic random token", "生成 256 位密码学安全随机令牌"),
                ["Options.ClusterCopyToken"] = new("Copy", "复制"),
                ["Options.ClusterCopyTokenHelp"] = new("Copy the current token to the clipboard", "把当前令牌复制到剪贴板"),
                ["Options.ClusterPoll"] = new("Replica poll interval (seconds):", "从节点轮询间隔（秒）："),
                ["Options.ClusterStale"] = new("Fail Unsafe after no reply (seconds):", "无响应后转为不安全（秒）："),
                ["Options.ClusterReplicaHelp"] = new("Normally the replica receives status only. If automatic failover is enabled and a valid encrypted configuration has been synchronized, it can temporarily capture and analyze locally after the primary remains unreachable.", "从节点通常只接收状态。启用自动接管且已同步有效的加密配置后，主节点持续不可达时，从节点可以临时在本机采集并分析。"),
                ["Options.ClusterSecurityHelp"] = new("LAN requests use HMAC signatures, so the shared token is not sent over the network. Weather status is still not encrypted; restrict the port to trusted source IPs and never expose it directly to the Internet.", "局域网请求使用 HMAC 签名，共享令牌不会在网络上传输。普通天气状态仍未加密；请把端口限制到可信来源 IP，切勿直接暴露到公网。"),
                ["Options.ClusterAutomaticFailover"] = new("Automatically take over locally when the primary is unreachable", "主节点不可达时自动在本机接管"),
                ["Options.ClusterFailoverAfter"] = new("Take over after continuous outage (seconds):", "连续失联多久后接管（秒）："),
                ["Options.ClusterRecoveryStable"] = new("Return after primary remains stable (seconds):", "主节点恢复稳定多久后回切（秒）："),
                ["Options.ClusterAutomaticFailoverHelp"] = new("Network failures can trigger failover; authentication and protocol errors never do. The monitor remains Unsafe until the first successful local analysis.", "只有网络故障会触发接管；认证和协议错误不会。第一次本地分析成功之前，安全监视器始终为不安全。"),
                ["Options.ClusterFailoverConfigSync"] = new("Allow encrypted failover camera/API configuration sync", "允许同步加密的接管相机/API 配置"),
                ["Options.ClusterFailoverConfigSyncHelp"] = new("Enabling this on the primary authorizes replicas holding the shared token to receive encrypted camera and AI credentials. Enable it on a replica to request and cache them. Dataset and machine-local safety settings are never synchronized.", "在主节点启用后，持有共享令牌的从节点可以接收加密的相机和 AI 凭据；在从节点启用后才会请求并缓存。数据集及机器本地安全设置永不同步。"),
                ["Options.ReplicaSyncedTitle"] = new("Primary-synchronized takeover settings (read-only)", "主节点同步的接管配置（只读）"),
                ["Options.ReplicaSyncedHelp"] = new("While the primary is online, this replica consumes its published safety verdict. The settings below are encrypted, synchronized and cached for local automatic takeover only; they cannot be changed on the replica.", "主节点在线时，从节点只采用主节点发布的安全结论。下列设置经过加密同步并缓存，仅供本机自动接管时使用；不能在从节点修改。"),
                ["Options.ReplicaManualConfigurationHelp"] = new("Encrypted synchronization is disabled. The local camera, thresholds and AI fields below are manual standby values; they do not change the primary verdict and are used only if this replica performs an enabled local takeover.", "加密配置同步已关闭。下方相机、阈值和 AI 字段是本机手动预置的备用值；它们不会改变主节点结论，仅在已启用的本地接管发生时使用。"),
                ["Options.ReplicaSyncDisabled"] = new("Synchronization is disabled; manual local standby settings apply.", "同步已关闭；将使用本机手动预置的备用配置。"),
                ["Options.ReplicaSyncReady"] = new("Synchronized and cached; ready for automatic local takeover.", "已同步并缓存；可供自动本地接管使用。"),
                ["Options.ReplicaSyncInvalid"] = new("The synchronized cache is unavailable: {0}", "同步缓存不可用：{0}"),
                ["Options.ReplicaSyncMissingToken"] = new("Enter the shared token before the synchronized cache can be read.", "请先填写共享令牌，才能读取同步缓存。"),
                ["Options.ReplicaSyncWaiting"] = new("Waiting for the Safety Monitor to connect and receive configuration from the primary.", "正在等待安全监视器连接并从主节点接收配置。"),
                ["Options.ReplicaSyncDecryptFailed"] = new("the token does not match or the encrypted cache failed authentication", "令牌不匹配，或加密缓存未通过认证"),
                ["Options.ReplicaSyncCacheInvalid"] = new("the cached configuration is malformed or incomplete", "缓存配置格式错误或内容不完整"),
                ["Options.ReplicaRevision"] = new("Configuration revision:", "配置修订："),
                ["Options.ReplicaUpdated"] = new("Last synchronized:", "最近同步："),
                ["Options.ReplicaCaptureMode"] = new("Capture mode:", "采集模式："),
                ["Options.ReplicaCaptureSource"] = new("Camera source:", "相机来源："),
                ["Options.ReplicaCaptureCredentials"] = new("Camera credentials:", "相机凭据："),
                ["Options.ReplicaCheckInterval"] = new("Check interval:", "检查间隔："),
                ["Options.ReplicaSolarGuard"] = new("Night-only guard:", "仅夜间分析："),
                ["Options.ReplicaSunAltitude"] = new("Sun altitude limit:", "太阳高度角限制："),
                ["Options.ReplicaCloudThresholds"] = new("Cloud thresholds:", "云量阈值："),
                ["Options.ReplicaMaxDataAge"] = new("Maximum data age:", "数据最大有效时间："),
                ["Options.ReplicaProvider"] = new("AI provider:", "AI 分析服务："),
                ["Options.ReplicaModel"] = new("AI model:", "AI 模型："),
                ["Options.ReplicaApiCredential"] = new("API credential:", "API 凭据："),
                ["Options.ReplicaProviderDetails"] = new("Provider details:", "分析服务细节："),
                ["Options.ReplicaCredentialConfigured"] = new("Configured and encrypted (value hidden)", "已配置并加密同步（内容隐藏）"),
                ["Options.ReplicaCredentialNotConfigured"] = new("Not configured", "未配置"),
                ["Options.ReplicaCredentialNotRequired"] = new("Not required by this provider", "此分析服务不需要 API 密钥"),
                ["Options.ReplicaMinutesValue"] = new("{0} minute(s)", "{0} 分钟"),
                ["Options.ReplicaEnabled"] = new("Enabled", "已开启"),
                ["Options.ReplicaDisabled"] = new("Disabled", "已关闭"),
                ["Options.ReplicaNotApplicable"] = new("Not applicable", "不适用"),
                ["Options.ReplicaThresholdsValue"] = new("Unsafe at ≥ {0}% / Safe below {1}%", "≥ {0}% 时不安全 / 低于 {1}% 时安全"),
                ["Options.ReplicaAutomatic"] = new("Automatic", "自动"),
                ["Options.ReplicaGeminiPacingValue"] = new("One Gemini request every {0} weather check(s)", "每 {0} 次天气检查调用一次 Gemini"),
                ["Options.ReplicaGeminiFreeDetailsValue"] = new("One free-pool run every {0} check(s); {1} cycle(s); order: {2}", "每 {0} 次检查运行一次免费模型池；共 {1} 轮；顺序：{2}"),
                ["Options.ReplicaOllamaDetailsValue"] = new("Endpoint: {0}; disable thinking: {1}", "服务地址：{0}；禁用思考：{1}"),

                ["Cluster.Waiting"] = new("Waiting for the first authenticated primary status", "正在等待第一份通过认证的主节点状态"),
                ["Cluster.Error"] = new("Replica error: {0}", "从节点错误：{0}"),
                ["Cluster.Synchronized"] = new("Primary {0} · received {1:F0}s ago · session {2}", "主节点 {0} · {1:F0} 秒前收到 · 会话 {2}"),
                ["Cluster.AuthenticationFailed"] = new("Primary authentication failed — check the shared token", "主节点认证失败——请检查共享令牌"),
                ["Cluster.ProtocolFailed"] = new("Primary protocol is incompatible: {0}", "主节点协议不兼容：{0}"),
                ["Cluster.TransportStale"] = new("Primary status is stale ({0:F0}s since last reply; limit {1}s)", "主节点状态已过期（距上次响应 {0:F0} 秒；上限 {1} 秒）"),
                ["Cluster.PrimaryNotMonitoring"] = new("Primary is connected but is not actively monitoring", "主节点未处于有效监控状态"),
                ["Cluster.PrimaryUnsafe"] = new("Primary reports Unsafe: {0}", "主节点报告不安全：{0}"),
                ["Cluster.PrimarySafe"] = new("Primary status is fresh and Safe", "主节点状态新鲜且安全"),
                ["Cluster.ReplicaSettings"] = new("Node: Replica | {0}", "节点：从节点｜{0}"),
                ["Cluster.FailoverConfigReady"] = new("Encrypted failover configuration {0} is ready", "加密接管配置 {0} 已就绪"),
                ["Cluster.FailoverConfigMissing"] = new("No usable failover configuration is cached", "尚无可用的接管配置缓存"),
                ["Cluster.FailoverStarting"] = new("Primary unavailable; starting local camera and analysis", "主节点不可达；正在启动本地相机和分析"),
                ["Cluster.FailoverActive"] = new("LOCAL FAILOVER active · primary unreachable for {0:F0}s", "本地接管中｜主节点已失联 {0:F0} 秒"),
                ["Cluster.FailoverRecovering"] = new("LOCAL FAILOVER active · primary recovery stable for {0:F0}/{1}s", "本地接管中｜主节点已稳定恢复 {0:F0}/{1} 秒"),
                ["Cluster.FailoverReturned"] = new("Primary remained stable; local failover stopped and remote synchronization resumed", "主节点持续稳定；已停止本地接管并恢复远程同步"),
                ["Cluster.FailoverActivationFailed"] = new("Local failover could not start: {0}", "本地接管无法启动：{0}"),

                ["Options.CaptureMode"] = new("Capture Mode:", "采集模式："),
                ["Options.RtspMode"] = new("RTSP Stream (continuous video)", "RTSP 视频流（连续视频）"),
                ["Options.HttpMode"] = new("HTTP Image Download (from URL)", "HTTP 图像下载（通过 URL）"),
                ["Options.FolderMode"] = new("Folder Watch (monitor latest image)", "文件夹监视（读取最新图像）"),
                ["Options.RtspUrl"] = new("RTSP Stream URL:", "RTSP 视频流 URL："),
                ["Options.ConfigureCredentials"] = new("Configure username/password in the AI Weather Monitor imaging tab", "请在 AI 天气监视器的成像页中设置用户名和密码"),
                ["Options.ImageUrl"] = new("Image URL:", "图像 URL："),
                ["Options.ImageUrlHint"] = new("Enter full URL to latest image", "输入最新图像的完整 URL"),
                ["Options.ImageUrlExample"] = new("Example: http://192.168.1.100/image.jpg", "示例：http://192.168.1.100/image.jpg"),
                ["Options.HttpBestFor"] = new("Perfect for indi-allsky, AllSky, or any web-hosted all-sky camera", "适用于 indi-allsky、AllSky 或其他提供网页图像的全天相机"),
                ["Options.UsernameOptional"] = new("Username (optional):", "用户名（可选）："),
                ["Options.PasswordOptional"] = new("Password (optional):", "密码（可选）："),
                ["Options.FolderPath"] = new("Folder Path:", "文件夹路径："),
                ["Options.FolderHint"] = new("Folder containing latest all-sky images", "包含最新全天图像的文件夹"),
                ["Options.Provider"] = new("Analysis Provider:", "分析服务："),
                ["Options.ProviderNameLocal"] = new("Local (site-trained ONNX)", "本地（本站训练 ONNX）"),
                ["Options.ProviderNameGitHub"] = new("GitHub Models (RETIRED)", "GitHub Models（已停止服务）"),
                ["Options.ProviderNameGeminiPaid"] = new("Google Gemini (billed project)", "Google Gemini（已启用结算）"),
                ["Options.ProviderNameGeminiFree"] = new("Google Gemini Free (free tier)", "Google Gemini 免费（免费层）"),
                ["Options.ProviderNameOllama"] = new("Ollama / Custom (local server)", "Ollama / 自定义（本地服务）"),
                ["Options.Interval"] = new("Check Interval (minutes):", "检查间隔（分钟）："),
                ["Options.SolarGuard"] = new("Night-only analysis", "仅夜间分析"),
                ["Options.UseSunAltitudeLimit"] = new("Only analyze when the Sun is below the altitude limit", "仅当太阳低于高度角限制时进行分析"),
                ["Options.SunAltitudeLimit"] = new("Maximum Sun altitude (degrees):", "太阳最大高度角（度）："),
                ["Options.SunAltitudeHelp"] = new("At or above this altitude, the plugin captures no analysis frame, calls neither the local nor online model, writes no dataset sample, and reports Unsafe. Typical values: 0° = sunset, -6° = civil twilight, -12° = nautical twilight.", "太阳达到或高于此高度角时，插件不会抓取分析帧、不会调用本地或在线模型、不会写入数据集，并直接报告不安全。典型值：0° = 日落，-6° = 民用暮光结束，-12° = 航海暮光结束。"),
                ["Options.HighThreshold"] = new("Cloud Coverage High Threshold (%):", "云量高阈值（不安全，%）："),
                ["Options.LowThreshold"] = new("Cloud Coverage Low Threshold (%):", "云量低阈值（安全，%）："),
                ["Options.HighThresholdHelp"] = new("While Safe, coverage at or above this value turns the status Unsafe.", "当前为安全时，云量达到或超过此值会转为不安全。"),
                ["Options.LowThresholdHelp"] = new("While Unsafe, coverage must drop below this value before Safe returns.", "当前为不安全时，云量必须降到此值以下才会恢复安全。"),
                ["Options.ThresholdBandHelp"] = new("Between the two values the status does not change. Keep Low below High; a 5–15 point band usually prevents flapping without leaving the state stuck.", "处于两个阈值之间时状态保持不变。低阈值应小于高阈值；通常保留 5–15 个百分点的区间，可避免反复抖动又不至于长期卡在不安全。"),
                ["Options.GenericFile"] = new("Generic File SafetyMonitor", "通用文件安全监视器"),
                ["Options.WriteStatus"] = new("Write Safe/Unsafe status file:", "写入安全/不安全状态文件："),
                ["Options.StatusOneLine"] = new("Writes a single line: Safe or Unsafe", "写入单行内容：Safe 或 Unsafe"),
                ["Options.StatusPointDriver"] = new("Point your ASCOM Generic File SafetyMonitor to this file", "请让 ASCOM 通用文件安全监视器读取此文件"),
                ["Options.FailSafe"] = new("Fail-safe", "故障安全"),
                ["Options.MaxDataAge"] = new("Maximum data age (minutes, 0 = automatic):", "数据最大有效时间（分钟，0 = 自动）："),
                ["Options.MaxDataAgeHelp"] = new("If no sky analysis succeeds within this time — a dead camera, an unreachable stream — the monitor reports Unsafe instead of holding the last known state. Automatic means three check intervals, never less than 10 minutes.", "如果在此时间内没有成功完成天空分析（例如相机失联或视频流不可达），监视器会报告不安全，而不会继续沿用上一次状态。自动值为三个检查间隔，且不少于 10 分钟。"),
                ["Options.ExternalMonitor"] = new("External ASCOM Safety Monitor", "外部 ASCOM 安全监视器"),
                ["Options.CombineExternal"] = new("Combine with an external safety monitor:", "与外部安全监视器联合判断："),
                ["Options.ExternalHelp"] = new("The result is combined with AND: imaging is Safe only when both the sky analysis and the external device report Safe. Use it to pair all-sky cloud/rain/fog detection with an environmental monitor (humidity, dew point, rain sensor).", "两者按逻辑与（AND）合并：仅当天空分析和外部设备都报告安全时，拍摄才为安全。可将全天相机的云、雨、雾识别与湿度、露点、雨量等环境监视器配合使用。"),
                ["Options.ExternalFail"] = new("If the external device cannot be connected or read, the monitor reports Unsafe.", "如果外部设备无法连接或读取，监视器会报告不安全。"),
                ["Options.GitHubRetired"] = new("GitHub retired this service on July 30, 2026 — it no longer works for anyone. Analyses fall back to the bundled Local ONNX model; switch to Ollama/Custom (local, free), Google Gemini or OpenAI.", "GitHub 已于 2026 年 7 月 30 日停止此服务，现已无法使用。分析会回退到内置的本地 ONNX 模型；也可改用 Ollama/自定义服务（本地、免费）、Google Gemini 或 OpenAI。"),
                ["Options.GitHubToken"] = new("GitHub Personal Access Token:", "GitHub 个人访问令牌："),
                ["Options.OpenAiKey"] = new("OpenAI API Key:", "OpenAI API 密钥："),
                ["Options.GeminiKey"] = new("Google Gemini Free API Key:", "Google Gemini 免费层 API 密钥："),
                ["Options.GeminiPaidKey"] = new("Google Gemini billed-project API Key:", "Google Gemini 结算项目 API 密钥："),
                ["Options.GeminiRequestEveryChecks"] = new("Online request: once every N weather checks:", "在线调用：每 N 次天气检查调用一次："),
                ["Options.GeminiRequestEveryChecksHelp"] = new("The local safety analysis still runs on every weather check. 1 calls the selected Gemini tier on every check; a larger N reduces quota or cost. Only checks that actually call Gemini can produce teacher labels.", "本地安全分析仍会在每次天气检查时运行。设为 1 会在每次检查时调用所选 Gemini 层级；增大 N 可节省免费配额或付费成本。只有实际调用 Gemini 的检查才能生成教师标签。"),
                ["Options.GeminiFreeModelOrder"] = new("Free-tier model order:", "免费层模型尝试顺序："),
                ["Options.GeminiFreeModelOrderHelp"] = new("Select a model and move it up or down. Every model has its own quota circuit; a quota-paused model is skipped until its retry time.", "选择模型后可上移或下移。每个模型都有独立的配额状态机；处于配额暂停期的模型会直接跳过，直到允许重试。"),
                ["Options.GeminiFreeCycles"] = new("Full model-pool cycles before fallback (1–10):", "回退前完整轮询模型池次数（1–10）："),
                ["Options.GeminiFreeCyclesHelp"] = new("Default 2. The plugin walks the list from top to bottom and repeats it this many times. It returns an online failure only after no model succeeds in all configured cycles.", "默认 2。插件会从上到下尝试整个列表，并按此次数重复；仅当全部轮次均无模型成功时才返回在线失败。"),
                ["Options.AnthropicKey"] = new("Anthropic API Key:", "Anthropic API 密钥："),
                ["Options.UsedOpenAi"] = new("Used with https://api.openai.com", "用于 https://api.openai.com"),
                ["Options.UsedGemini"] = new("Free-tier project on the Generative Language API. Availability and rate limits are not production guarantees.", "用于 Generative Language API 免费层项目；可用性与速率限制不提供生产级保证。"),
                ["Options.UsedGeminiPaid"] = new("Strict single-model policy for a billed project: one request to the selected model, with no ordering, downgrade, retry, rotation or backoff. The plugin cannot detect billing; verify this key's project in Google AI Studio.", "结算项目的严格单模型策略：每次只请求所选模型一次，不排序、不降级、不重试、不轮换、不退避。插件无法自行识别结算状态；请在 Google AI Studio 中确认该密钥所属项目。"),
                ["Options.UsedAnthropic"] = new("Used with https://api.anthropic.com", "用于 https://api.anthropic.com"),
                ["Options.OllamaUrl"] = new("Ollama Base URL:", "Ollama 基础 URL："),
                ["Options.OllamaDefault"] = new("Default: http://localhost:11434/v1", "默认：http://localhost:11434/v1"),
                ["Options.OllamaCompatible"] = new("Also works with LM Studio, llama.cpp, and LocalAI endpoints", "也兼容 LM Studio、llama.cpp 和 LocalAI 端点"),
                ["Options.OllamaNoKey"] = new("No API key required. Use a vision-capable model (e.g. llava, qwen2.5vl)", "无需 API 密钥。请使用支持视觉的模型（如 llava、qwen2.5vl）"),
                ["Options.DisableThinking"] = new("Disable model thinking (recommended):", "禁用模型思考（推荐）："),
                ["Options.ThinkingHelp"] = new("Thinking-capable models (Gemma 4, Qwen 3.x, DeepSeek) reason at length before answering, which can multiply response times. Turn off only on fast hardware.", "带思考能力的模型（Gemma 4、Qwen 3.x、DeepSeek）会在回答前进行较长推理，响应时间可能成倍增加。仅在硬件足够快时启用思考。"),
                ["Options.AiModel"] = new("AI Model:", "AI 模型："),
                ["Options.CameraView"] = new("Camera view:", "相机视角："),
                ["Options.CameraAllSky"] = new("All-sky fisheye", "全天鱼眼（圆形天空）"),
                ["Options.CameraFixed"] = new("Fixed camera / partial sky", "固定视角（局部天空）"),
                ["Options.CameraViewHelp"] = new("Select the visible sky geometry, not the camera brand. Fixed view excludes trees, buildings and ground. This changes online/Ollama prompts, not the bundled ONNX model.", "按天空画面的形状选择，不是按相机品牌。固定视角只分析地平线上方的天空，排除树木、建筑和地面。仅改变在线/Ollama 提示词，不改变内置 ONNX 模型。"),
                ["Options.SiteDarkness"] = new("Site darkness:", "台址光照环境："),
                ["Options.SiteLightPolluted"] = new("Light-polluted site", "有光污染（云层常被照亮）"),
                ["Options.SiteDark"] = new("Dark site", "暗场（云层可能比天空更暗）"),
                ["Options.SiteDarknessHelp"] = new("At dark sites, missing stars can indicate cloud rather than clear sky. A sky with no readable information is Unknown/Unsafe, not a measured 100% cloud label. Moon context still applies.", "暗场的暗区可能是遮住星点的云，不能仅凭黑色判断晴朗。完全不可读的天空报告未知/不安全，而非实测 100% 云量；仍结合月亮位置和亮度判断。"),
                ["Options.ReplicaPromptValue"] = new("Synchronized prompt (read-only): {0}; {1}; site notes: {2} (content hidden)", "同步提示词（只读）：{0}；{1}；台址备注：{2}（隐藏内容）"),
                ["Runtime.SkyUnreadable"] = new("Sky unreadable; online analysis reports Unknown, not measured cloud coverage", "天空不可读：在线分析报告未知，云量数值不是实测结果"),
                ["Options.SiteNotes"] = new("Site notes (optional):", "台址备注（可选）："),
                ["Options.SiteNotesHelp"] = new("Anything about your site or camera the AI cannot guess, in your own words. Example: \"Clouds toward the south are lit orange by the city and can look like overcast\" or \"In daylight the sun reflects off the dome and creates a hazy halo\". Sent with every analysis, for all providers. Keep it short — a few sentences.", "用自己的话补充 AI 无法猜到的台址或相机特征。例如：“南侧云层会被城市灯光照成橙色，容易被误判为阴天”或“白天太阳会在圆顶上产生朦胧光晕”。这些内容会随每次分析发送给所有服务商，请控制在几句话内。"),
                ["Options.Dataset"] = new("Dataset / Teacher–Student", "数据集 / 教师–学生"),
                ["Options.DatasetCollect"] = new("Collect local teacher/student dataset", "收集本地教师/学生数据集"),
                ["Options.DatasetPause"] = new("Pause collection (keep configuration)", "暂停收集（保留配置）"),
                ["Options.DatasetHelp"] = new("Only successful online responses become trainable teacher labels. The bundled Local ONNX model runs in shadow mode and never changes the active safety decision while the teacher succeeds.", "只有成功的在线响应才会成为可训练的教师标签。本地 ONNX 模型以影子模式运行；只要教师模型成功，它就不会改变当前安全判断。"),
                ["Options.DatasetDirectory"] = new("Dataset directory:", "数据集目录："),
                ["Options.DatasetInterval"] = new("Periodic sample interval (minutes):", "定期采样间隔（分钟）："),
                ["Options.DatasetSampleEveryChecks"] = new("Periodic sample: every N successful checks:", "定期样本：每 N 次成功检查采 1 次："),
                ["Options.DatasetSampleEveryChecksHelp"] = new("This ratio shares the weather-check clock: approximate periodic sample interval = check interval × N. Initial frames and meaningful change/review events are still retained independently.", "该比例与天气检查共用同一个时钟：定期样本的约间隔＝检查间隔 × N。初始帧以及有意义的变化/人工复核事件仍会独立保留。"),
                ["Options.DatasetMaxSize"] = new("Maximum dataset size (GB):", "数据集最大容量（GB）："),
                ["Options.DatasetMinFree"] = new("Minimum free disk space (GB):", "磁盘最小剩余空间（GB）："),
                ["Options.DatasetWidth"] = new("Training image width:", "训练图像宽度："),
                ["Options.DatasetHeight"] = new("Training image height:", "训练图像高度："),
                ["Options.DatasetScale"] = new("Training image scale (5–100%):", "训练图像等比缩放（5–100%）："),
                ["Options.DatasetScaleHelp"] = new("Width and height are both multiplied by this percentage, so the original aspect ratio is never distorted. 100% keeps the source size; 50% halves both dimensions.", "宽和高都会乘以该百分比，因此绝不会拉伸原始宽高比。100% 保持原尺寸；50% 表示宽、高各减半。"),
                ["Options.DatasetJpeg"] = new("JPEG quality (40–100):", "JPEG 质量（40–100）："),
                ["Options.DatasetDisagreement"] = new("Cloud disagreement event (%):", "云量分歧事件阈值（%）："),
                ["Options.DatasetHamming"] = new("Similar-image deduplication strength (0–64, recommended 4):", "相似图片去重强度（0–64，推荐 4）："),
                ["Options.DatasetHammingHelp"] = new("Prevents nearly identical sky frames from being saved repeatedly. Higher values make the recorder more likely to treat neighboring frames as duplicates and skip them. 0 is most conservative, 4 is recommended, and values above 8 are usually unnecessary. Internally this uses a 64-bit image fingerprint; the technical Hamming distance remains recorded in the implementation.", "用于避免连续保存几乎一样的天空画面。数值越大，越容易把相邻画面当成重复图并跳过；0 最保守，4 是推荐值，通常不建议超过 8。底层仍使用 64 位图像指纹及汉明距离比较，但普通使用时无需理解算法细节。"),
                ["Options.DatasetRaw"] = new("Save sanitized raw teacher JSON", "保存脱敏后的教师原始 JSON"),
                ["Options.DatasetRawHelp"] = new("Useful for debugging prompts and parsers. API keys, coordinates and RTSP credentials are removed; turning it off saves a little disk space.", "用于排查提示词和解析器问题。API 密钥、坐标和 RTSP 凭据会被移除；关闭可略微节省磁盘空间。"),
                ["Options.DatasetQuarantine"] = new("Keep malformed/inconsistent responses in quarantine", "将格式错误或不一致的响应保留到隔离区"),
                ["Options.DatasetQuarantineHelp"] = new("Quarantined records are kept for diagnosis and manual review but are excluded from the trainable label set until corrected.", "隔离记录仅用于诊断和人工复核；在修正前不会进入可训练标签集。"),
                ["Options.DatasetPrivacy"] = new("Images stay local. Coordinates, API keys and RTSP credentials are excluded. The recorder stops before its quota or free-space guard is exceeded.", "图像仅保存在本地；不会记录坐标、API 密钥和 RTSP 凭据。达到容量配额或最小剩余空间保护线之前，记录器会自动停止。"),
                ["Options.About"] = new("About AI Weather", "关于 AI 天气"),
                ["Options.AboutText"] = new("AI Weather analyzes all-sky camera images using artificial intelligence to determine real-time weather conditions and protect your equipment during unattended imaging sessions.", "AI 天气通过人工智能分析全天相机图像，判断实时天气状况，并在无人值守拍摄时保护设备。"),
                ["Options.HowItWorks"] = new("How It Works", "工作原理"),
                ["Options.HowItWorksText"] = new("The plugin periodically captures an image from your all-sky camera, sends it to a vision-capable AI model, and receives a structured weather assessment including cloud coverage percentage, weather condition, and rain/fog detection. Based on configurable thresholds, it reports a Safe or Unsafe status to NINA's safety monitor, which can automatically pause or abort an imaging sequence.", "插件会定期从全天相机采集图像，发送给支持视觉的 AI 模型，并得到包含云量、天气类别以及雨雾检测结果的结构化评估。插件依据可配置阈值向 N.I.N.A. 安全监视器报告安全或不安全状态，从而自动暂停或中止拍摄序列。"),
                ["Options.CaptureModes"] = new("Capture Modes", "采集模式"),
                ["Options.CaptureRtspInfo"] = new("○   RTSP Stream - live video from network IP cameras with real-time preview", "○   RTSP 视频流——网络相机实时视频与预览"),
                ["Options.CaptureHttpInfo"] = new("○   HTTP Image - periodic download from a URL (indi-allsky, AllSky, web cameras)", "○   HTTP 图像——定期从 URL 下载图像（indi-allsky、AllSky、网络相机）"),
                ["Options.CaptureFolderInfo"] = new("○   Folder Watch - monitors a local folder for the latest image saved by any camera software", "○   文件夹监视——读取任意相机软件保存到本地文件夹的最新图像"),
                ["Options.Providers"] = new("AI Providers", "AI 服务商"),
                ["Options.ProviderLocal"] = new("○   Local - bundled site-trained MobileNetV3 ONNX model; offline CPU inference", "○   本地——内置本站训练的 MobileNetV3 ONNX 模型，使用 CPU 离线推理"),
                ["Options.ProviderGitHub"] = new("○   GitHub Models - RETIRED by GitHub on July 30, 2026; analyses fall back to Local", "○   GitHub Models——GitHub 已于 2026 年 7 月 30 日停止服务，分析会回退到本地"),
                ["Options.ProviderOpenAi"] = new("○   OpenAI - GPT-4o, GPT-4.1, o1, o3, o4-mini (models fetched live)", "○   OpenAI——GPT-4o、GPT-4.1、o1、o3、o4-mini（实时获取模型列表）"),
                ["Options.ProviderGemini"] = new("○   Google Gemini - strict billed-project single model; no ordering, downgrade, retry, rotation or backoff", "○   Google Gemini——结算项目的严格单模型策略；不排序、不降级、不重试、不轮换、不退避"),
                ["Options.ProviderGeminiFree"] = new("○   Google Gemini Free - free tier; lower limits and best-effort availability, with independent pacing and diagnostics", "○   Google Gemini 免费——免费层，限额较低且可用性为尽力而为，调用频率与诊断状态独立"),
                ["Options.ProviderAnthropic"] = new("○   Anthropic Claude - Claude Sonnet 4.5, Sonnet 4, Haiku 4.5, 3.5 series (models fetched live)", "○   Anthropic Claude——Sonnet 4.5、Sonnet 4、Haiku 4.5、3.5 系列（实时获取模型列表）"),
                ["Options.ProviderOllama"] = new("○   Ollama - local OpenAI-compatible server, no API key (LM Studio, llama.cpp, LocalAI also work)", "○   Ollama——本地 OpenAI 兼容服务，无需 API 密钥（也支持 LM Studio、llama.cpp、LocalAI）"),
                ["Options.SafetyFeatures"] = new("Safety Features", "安全功能"),
                ["Options.SafetyDetect"] = new("○   Detects cloud coverage, rain (including lens droplets), and fog", "○   检测云量、降雨（包括镜头水滴）和雾"),
                ["Options.SafetyRainFog"] = new("○   Rain and fog trigger Unsafe regardless of cloud threshold", "○   检测到雨或雾时，无论云量阈值如何都会报告不安全"),
                ["Options.SafetyFallback"] = new("○   Automatic fallback to local analysis if the AI provider fails or times out", "○   AI 服务失败或超时时自动回退到本地分析"),
                ["Options.SafetyFile"] = new("○   Optional status file output for ASCOM Generic File SafetyMonitor integration", "○   可选输出状态文件，与 ASCOM 通用文件安全监视器集成"),
                ["Options.ConnectHelp"] = new("Connect the safety monitor under Equipment > Safety Monitor > All Sky Camera Safety Monitor to start protecting your imaging sessions.", "请在“设备 > 安全监视器”中连接“全天相机安全监视器”，以开始保护拍摄任务。"),
                ["Options.DialogSkyFolder"] = new("Select folder to monitor for sky images", "选择要监视天空图像的文件夹"),
                ["Options.DialogDatasetFolder"] = new("Choose the AI Weather teacher/student dataset directory", "选择 AI 天气教师/学生数据集目录"),
                ["Options.DialogStatusFile"] = new("Choose status file to write (Safe/Unsafe)", "选择要写入的状态文件（Safe/Unsafe）"),
                ["Options.DialogTextFiles"] = new("Text files (*.txt)|*.txt|All files (*.*)|*.*", "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"),

                ["Review.Title"] = new("AI Weather Dataset Label Reviewer", "AI 天气数据集标签检查器"),
                ["Review.Search"] = new("Search", "搜索"),
                ["Review.Status"] = new("Review status", "复核状态"),
                ["Review.Refresh"] = new("Refresh index", "刷新索引"),
                ["Review.OpenFolder"] = new("Open dataset folder", "打开数据集目录"),
                ["Review.ColTime"] = new("Local time", "本地时间"),
                ["Review.ColReview"] = new("Review", "复核"),
                ["Review.ColTeacher"] = new("Teacher", "教师"),
                ["Review.ColCloud"] = new("Cloud", "云量"),
                ["Review.ColStudent"] = new("Student", "学生"),
                ["Review.ColDifference"] = new("Difference", "差值"),
                ["Review.ColReason"] = new("Selection reason", "采样原因"),
                ["Review.Previous"] = new("← Previous", "← 上一张"),
                ["Review.Next"] = new("Next →", "下一张 →"),
                ["Review.SampleOriginal"] = new("Sample and original labels", "样本与原始标签"),
                ["Review.Selection"] = new("Selection: {0}", "采样原因：{0}"),
                ["Review.HumanReview"] = new("Human review", "人工复核"),
                ["Review.Condition"] = new("Weather condition", "天气类别"),
                ["Review.CloudCoverage"] = new("Cloud 0–100%", "云量 0–100%"),
                ["Review.Rain"] = new("Rain detected", "检测到雨"),
                ["Review.Fog"] = new("Fog detected", "检测到雾"),
                ["Review.Notes"] = new("Review notes (automatically redacted)", "复核备注（会自动脱敏）"),
                ["Review.ImmutableHelp"] = new("Accept Teacher keeps the teacher pseudo-label; Save Correction writes a separate human label; the original teacher label is never overwritten.", "“接受教师标签”会保留教师伪标签；“保存人工纠正”会写入独立的人工标签；原始教师标签永远不会被覆盖。"),
                ["Review.Accept"] = new("Accept teacher label", "接受教师标签"),
                ["Review.SaveCorrection"] = new("Save human correction", "保存人工纠正"),
                ["Review.Reject"] = new("Reject sample", "拒绝此样本"),
                ["Review.Reset"] = new("Reset to unreviewed", "重置为未复核"),
                ["Review.Delete"] = new("Delete sample permanently", "永久删除样本"),
                ["Review.DeleteHelp"] = new("Permanently deletes this sample's teacher label, review sidecar and uniquely owned image to release disk space.", "永久删除此样本的教师标签、人工复核附属文件和仅由它占用的图片，以释放磁盘空间。"),
                ["Review.DeleteConfirmTitle"] = new("Permanently delete dataset sample?", "确认永久删除数据集样本？"),
                ["Review.DeleteConfirm"] = new("Permanently delete sample {0} captured at {1}?\n\nIts teacher label, human review sidecar and any uniquely owned image will be removed. A shared image is retained. This cannot be undone.", "要永久删除样本 {0} 吗？拍摄时间：{1}\n\n它的教师标签、人工复核附属文件和仅由它占用的图片都会被删除；共享图片会保留。此操作无法撤销。"),
                ["Review.FilterAll"] = new("All", "全部"),
                ["Review.Unreviewed"] = new("Unreviewed", "未复核"),
                ["Review.Accepted"] = new("Accepted", "已接受"),
                ["Review.Corrected"] = new("Corrected", "已纠正"),
                ["Review.Rejected"] = new("Rejected", "已拒绝"),
                ["Review.Loading"] = new("Loading dataset…", "正在加载数据集…"),
                ["Review.Scanning"] = new("Scanning labels and review sidecars…", "正在扫描标签和人工复核附属文件…"),
                ["Review.NoLabels"] = new("No labels yet. Only successful online teacher samples appear here.", "还没有标签。这里只会显示在线教师模型成功生成的样本。"),
                ["Review.Loaded"] = new("Loaded {0} labels from the indexable dataset view.", "已从可索引数据集视图加载 {0} 个标签。"),
                ["Review.LoadFailed"] = new("Could not load dataset: {0}", "无法加载数据集：{0}"),
                ["Review.PreviewFailed"] = new("Could not load preview: {0}", "无法加载预览：{0}"),
                ["Review.LabelError"] = new("Label error: {0}", "标签错误：{0}"),
                ["Review.CloudRange"] = new("Cloud coverage must be a number from 0 to 100.", "云量必须是 0 到 100 之间的数字。"),
                ["Review.Saving"] = new("Saving review atomically…", "正在以原子方式保存复核结果…"),
                ["Review.Saved"] = new("Saved {0} review, revision {1}. Original teacher label preserved.", "已保存“{0}”复核（修订版 {1}）；原始教师标签保持不变。"),
                ["Review.SaveFailed"] = new("Review was not saved: {0}", "复核结果未保存：{0}"),
                ["Review.Deleting"] = new("Permanently deleting sample {0}…", "正在永久删除样本 {0}…"),
                ["Review.Deleted"] = new("Deleted sample {0}: {1} files removed, {2:F3} MiB released.", "已删除样本 {0}：移除 {1} 个文件，释放 {2:F3} MiB。"),
                ["Review.DeletedSharedImage"] = new("Deleted sample {0}: {1} files removed, {2:F3} MiB released. Its image was retained because another label references the same file.", "已删除样本 {0}：移除 {1} 个文件，释放 {2:F3} MiB。由于另一个标签仍引用同一图片，该图片已保留。"),
                ["Review.DeleteFailed"] = new("Sample was not deleted: {0}", "样本未删除：{0}"),
                ["Review.OpenFailed"] = new("Could not open dataset folder: {0}", "无法打开数据集目录：{0}"),
                ["Review.Summary"] = new("Visible {0} / total {1} · unreviewed {2} · accepted {3} · corrected {4} · rejected {5} · damaged {6}", "显示 {0} / 共 {1} · 未复核 {2} · 已接受 {3} · 已纠正 {4} · 已拒绝 {5} · 损坏 {6}"),
                ["Review.NoTeacher"] = new("No valid teacher result", "没有有效的教师结果"),
                ["Review.NoStudent"] = new("No student result", "没有学生结果"),
                ["Review.NoAstro"] = new("No astro context", "没有天文环境信息"),
                ["Review.NoImage"] = new("No image metadata", "没有图像元数据"),
                ["Review.UnreviewedHelp"] = new("Unreviewed — teacher pseudo-label has not been checked by a human.", "未复核——教师伪标签尚未经过人工检查。"),
                ["Review.Provenance"] = new("Teacher: {0}/{1} · prompt {2} · {3} ms · online={4} · fallback={5}", "教师：{0}/{1} · 提示词 {2} · {3} 毫秒 · 在线成功={4} · 回退={5}"),
                ["Review.TeacherSummary"] = new("{0} · cloud {1:F0}% · confidence {2:F0}% · rain {3} · fog {4}\n{5}", "{0} · 云量 {1:F0}% · 置信度 {2:F0}% · 雨 {3} · 雾 {4}\n{5}"),
                ["Review.StudentSummary"] = new("{0}: {1} · cloud {2:F0}% · confidence {3:F0}%", "{0}：{1} · 云量 {2:F0}% · 置信度 {3:F0}%"),
                ["Review.AstroSummary"] = new("Sun {0:F1}° ({1}) · Moon {2:F0}% {3} at {4:F1}°", "太阳高度 {0:F1}°（{1}）· 月相 {3}，照明度 {2:F0}%，高度 {4:F1}°"),
                ["Review.ImageSummary"] = new("Saved {0}×{1} from {2}×{3} · SHA-256 {4} · pHash {5} · near duplicate {6}", "保存尺寸 {0}×{1}（原图 {2}×{3}）· SHA-256 {4} · 感知哈希 {5} · 近重复 {6}"),
                ["Review.ReviewSummary"] = new("{0} · revision {1} · {2}{3}", "{0} · 修订版 {1} · {2}{3}"),
                ["Review.HumanLabel"] = new(" · human {0}/{1:F0}%", " · 人工标签 {0}/{1:F0}%"),

                ["Runtime.Ready"] = new("Ready", "就绪"),
                ["Runtime.Initialized"] = new("AI Weather Monitor initialized...", "AI 天气监视器已初始化…"),
                ["Runtime.Connected"] = new("Connected", "已连接"),
                ["Runtime.Disconnected"] = new("Disconnected", "已断开"),
                ["Runtime.NoAnalysis"] = new("No analysis available", "暂无分析结果"),
                ["Runtime.SourceWaiting"] = new("Source: waiting", "来源：等待分析"),
                ["Runtime.SourceSolarSuspended"] = new("Source: suspended by Sun altitude", "来源：因太阳高度暂停"),
                ["Runtime.Source"] = new("Source: {0}/{1}{2}", "来源：{0}/{1}{2}"),
                ["Runtime.OnlineTeacher"] = new("online teacher", "在线教师"),
                ["Runtime.Fallback"] = new(" | online teacher request failed ({0}); local fallback active", "｜在线教师调用失败（{0}），当前使用本地模型"),
                ["Runtime.FallbackDescription"] = new("Online {0} request failed ({1}); using the local model. {2}", "在线 {0} 调用失败（{1}），已回退到本地模型。{2}"),
                ["Runtime.FallbackQuota"] = new(" | online API quota paused until {0}; local fallback active", "｜在线 API 配额暂停至 {0}，当前使用本地模型"),
                ["Runtime.FallbackQuotaNoTime"] = new(" | online API quota temporarily unavailable; local fallback active", "｜在线 API 配额暂不可用，当前使用本地模型"),
                ["Runtime.FallbackQuotaDescription"] = new("Online {0} API quota is temporarily unavailable; using the local model. The next online attempt is after {1}. {2}", "在线 {0} API 配额暂不可用，已回退到本地模型；下次在线尝试时间为 {1}。{2}"),
                ["Runtime.FallbackQuotaDescriptionNoTime"] = new("Online {0} API quota is temporarily unavailable; using the local model. {1}", "在线 {0} API 配额暂不可用，已回退到本地模型。{1}"),
                ["Runtime.FallbackDailyQuota"] = new(" | online daily API quota exhausted; reset expected at {0}; local fallback active", "｜在线每日 API 配额已用尽，预计于 {0} 重置；当前使用本地模型"),
                ["Runtime.FallbackDailyQuotaDescription"] = new("Online {0} daily API quota is exhausted; using the local model. Google resets RPD at Pacific midnight, expected at {1}. {2}", "在线 {0} 每日 API 配额已用尽，已回退到本地模型；Google 按太平洋时间午夜重置 RPD，预计恢复时间为 {1}。{2}"),
                ["Runtime.ScheduledLocal"] = new(" | scheduled local check (Gemini every {0} checks)", "｜按计划使用本地检查（Gemini 每 {0} 次调用一次）"),
                ["Runtime.ScheduledLocalDescription"] = new("[Scheduled: Local] {0} is configured to run once every {1} weather checks; this check used local analysis. {2}", "[计划：本地] {0} 已设为每 {1} 次天气检查在线调用一次；本次使用本地分析。{2}"),
                ["Runtime.LocalRainDescription"] = new("Rain detected - unsafe for imaging", "检测到降雨——不适合拍摄"),
                ["Runtime.LocalFogDescription"] = new("Fog detected - poor imaging conditions", "检测到雾——拍摄条件较差"),
                ["Runtime.LocalCloudDescription"] = new("{0} - {1:F1}% cloud coverage", "{0}——云量 {1:F1}%"),
                ["Runtime.LocalProcessing"] = new("Local Image Processing", "本地图像处理"),
                ["Runtime.AiSettings"] = new("AI: {0} | Check: {1}m | Cloud Limits: {2:F0}% / {3:F0}%", "AI：{0}｜间隔：{1} 分钟｜云量阈值：{2:F0}% / {3:F0}%"),
                ["Runtime.SunLimitSummary"] = new(" | Sun < {0:F1}°", "｜太阳 < {0:F1}°"),
                ["Runtime.NotConnected"] = new("Not connected", "未连接"),
                ["Runtime.WaitingFirst"] = new("Waiting for the first sky analysis", "正在等待第一次天空分析"),
                ["Runtime.SolarSuspended"] = new("Daytime suspension: Sun {0:F1}° is at or above the {1:F1}° limit; capture, models, API and dataset recording are disabled", "白天暂停：太阳高度 {0:F1}° 已达到或超过限制 {1:F1}°；已停止抓帧、模型、API 和数据集记录"),
                ["Runtime.SolarUnavailable"] = new("Analysis suspended: the Sun altitude cannot be computed from the active N.I.N.A. profile; safety remains Unsafe", "分析已暂停：无法根据当前 N.I.N.A. 配置计算太阳高度；安全状态保持不安全"),
                ["Runtime.SolarSuspendedShort"] = new("Daytime analysis suspended", "白天分析已暂停"),
                ["Runtime.Stale"] = new("Stale data: last analysis {0:F0} min ago, limit {1:F0} min — check the camera source", "数据已过期：上次分析在 {0:F0} 分钟前，有效上限为 {1:F0} 分钟——请检查相机源"),
                ["Runtime.ExternalUnsafe"] = new("External safety monitor reports unsafe", "外部安全监视器报告不安全"),
                ["Runtime.ExternalUnreadable"] = new("External safety monitor cannot be connected or read", "外部安全监视器无法连接或读取"),
                ["Runtime.NoUsable"] = new("No usable analysis", "没有可用的分析结果"),
                ["Runtime.Rain"] = new("Rain detected", "检测到雨"),
                ["Runtime.Fog"] = new("Fog detected", "检测到雾"),
                ["Runtime.CloudUnsafe"] = new("Cloud coverage {0:F0}% (safe below {1}%)", "云量 {0:F0}%（低于 {1}% 才恢复安全）"),
                ["Runtime.SkyClear"] = new("Sky clear and data current", "天空状况良好，数据有效"),
                ["Runtime.DatasetOff"] = new("Dataset: off", "数据集：关闭"),
                ["Runtime.DatasetStatus"] = new("Dataset: {0} | today {1}, total {2}, quarantine {3}, dropped {4} | {5:F2} GB{6}", "数据集：{0}｜今日 {1}，总计 {2}，隔离 {3}，丢弃 {4}｜{5:F2} GB{6}"),
                ["Runtime.CloudDifference"] = new(" | Δcloud {0:F0}%", "｜云量差 {0:F0}%"),
                ["Runtime.StreamStopped"] = new("Stream stopped", "视频流已停止"),
                ["Runtime.StreamConnecting"] = new("Connecting to stream...", "正在连接视频流…"),
                ["Runtime.StreamActive"] = new("Live stream active", "实时视频流已启动"),
                ["Runtime.WaitingAnalysis"] = new("Waiting for first analysis...", "正在等待第一次分析…"),
                ["Runtime.VideoError"] = new("Video view error", "视频视图错误"),
                ["Runtime.Error"] = new("Error: {0}", "错误：{0}"),
                ["Runtime.MonitoringActive"] = new("Monitoring active", "监视运行中"),
                ["Runtime.RtspRequired"] = new("RTSP URL required", "必须填写 RTSP URL"),
                ["Runtime.Connecting"] = new("Connecting...", "正在连接…"),
                ["Runtime.ConnectionFailed"] = new("Connection failed", "连接失败"),
                ["Runtime.ConnectionError"] = new("Connection error", "连接错误"),
                ["Runtime.Capturing"] = new("Capturing frame...", "正在截取图像帧…"),
                ["Runtime.CameraNotConnected"] = new("Not connected to camera", "相机未连接"),
                ["Runtime.CaptureFailed"] = new("Failed to capture frame", "截取图像帧失败"),
                ["Runtime.AnalysisComplete"] = new("Analysis complete", "分析完成"),
                ["Runtime.ImageMissing"] = new("Image captured but not found", "已截取图像，但未找到图像文件"),
                ["Runtime.MonitoringStopped"] = new("Monitoring stopped", "监视已停止"),
                ["Runtime.InitialCapture"] = new("Capturing initial image...", "正在截取初始图像…"),
                ["Runtime.ImageSaved"] = new("Image saved to {0}", "图像已保存到 {0}"),
                ["Runtime.ImageSaveError"] = new("Error saving image: {0}", "保存图像时出错：{0}"),
                ["Runtime.ModelsBuiltIn"] = new("Using built-in model list", "正在使用内置模型列表"),
                ["Runtime.ModelsLocal"] = new("Using bundled site-trained Local ONNX model", "正在使用内置的本站训练 ONNX 模型"),
                ["Options.TestAnalysis"] = new("Test selected provider", "测试当前分析服务"),
                ["Options.TestAnalysisHelp"] = new("Sends a synthetic sky image using the selected provider or free pool. Consumes API quota; does not update safety or the dataset.", "发送合成天空图像，按当前服务或免费模型池设置测试；会消耗 API 配额，不改变安全状态或写入数据集。"),
                ["Options.TestAnalysisRunning"] = new("Testing selected analysis service...", "正在测试当前分析服务…"),
                ["Options.TestAnalysisInitFailed"] = new("Initialization failed; check credentials and configuration.", "初始化失败，请检查凭据和配置。"),
                ["Options.TestAnalysisSucceeded"] = new("{0}/{1} answered successfully.", "{0}/{1} 已成功返回分析。"),
                ["Options.TestAnalysisUnavailable"] = new("Provider test failed (safety state unchanged): {0}", "服务测试未成功（未改变安全状态）：{0}"),
                ["Options.TestAnalysisFailed"] = new("Analysis test failed: {0}", "分析测试失败：{0}"),
                ["Runtime.FreePoolFailed"] = new("Gemini Free: no model succeeded in this check.", "Gemini 免费模型池本轮均未成功。"),
                ["Runtime.FreePoolRetry"] = new("All entries are quota-paused; earliest retry: {0}.", "所有模型均因配额暂停，最早重试：{0}。"),
                ["Runtime.ProviderHealthCount"] = new("{0}: {1} consecutive online failures; local fallback active.", "{0} 已连续 {1} 次在线分析失败，正在使用本地回退。"),
                ["Runtime.GeminiFreePoolReady"] = new("Gemini Free pool: {0} ordered models × {1} cycles", "Gemini 免费模型池：{0} 个有序模型 × {1} 轮"),
                ["Runtime.ProviderNameGeminiPaid"] = new("Gemini", "Gemini"),
                ["Runtime.ProviderNameGeminiFree"] = new("Gemini Free", "Gemini 免费"),
                ["Runtime.ModelsCached"] = new("Loaded {0} models (cached)", "已加载 {0} 个模型（缓存）"),
                ["Runtime.ModelsFetching"] = new("Fetching models from {0}...", "正在从 {0} 获取模型…"),
                ["Runtime.ModelsLoaded"] = new("Loaded {0} vision-capable models from {1}", "已从 {1} 加载 {0} 个视觉模型"),
                ["Runtime.ModelsFallback"] = new("Using built-in {0} model list ({1} models)", "正在使用内置 {0} 模型列表（{1} 个模型）"),
                ["Runtime.ModelsFailed"] = new("Model fetch failed; using built-in list ({0})", "获取模型失败，正在使用内置列表（{0}）"),
                ["Runtime.KeyEmpty"] = new("Key is empty", "密钥为空"),
                ["Runtime.KeyTesting"] = new("Testing key...", "正在测试密钥…"),
                ["Runtime.KeyHttpFailed"] = new("Key test failed: HTTP {0} {1}", "密钥测试失败：HTTP {0} {1}"),
                ["Runtime.KeyOkModels"] = new("Key OK (models: {0})", "密钥有效（模型数：{0}）"),
                ["Runtime.KeyOk"] = new("Key OK", "密钥有效"),
                ["Runtime.KeyFailed"] = new("Key test failed: {0}", "密钥测试失败：{0}"),

                ["Log.MonitoringStopped"] = new("⏹ Monitoring stopped", "⏹ 监视已停止"),
                ["Log.MonitoringFirst"] = new("✓ Monitoring started — waiting for first weather check...", "✓ 监视已启动——正在等待第一次天气检查…"),
                ["Log.SolarSuspended"] = new("Sun altitude guard active ({0:F1}° >= {1:F1}°): analysis and dataset recording suspended", "太阳高度保护已生效（{0:F1}° >= {1:F1}°）：分析与数据集记录已暂停"),
                ["Log.SolarUnavailable"] = new("Sun altitude guard active, but astronomical context is unavailable: analysis suspended fail-safe", "太阳高度保护已启用，但天文信息不可用：已按故障安全原则暂停分析"),
                ["Log.SolarResumed"] = new("Sun is below the altitude limit; weather analysis resumed", "太阳已低于高度角限制，天气分析已恢复"),
                ["Log.DisconnectingRtsp"] = new("Disconnecting from RTSP stream...", "正在断开 RTSP 视频流…"),
                ["Log.Disconnected"] = new("✓ Disconnected successfully", "✓ 已成功断开"),
                ["Log.RtspRequired"] = new("ERROR: RTSP URL is required", "错误：必须填写 RTSP URL"),
                ["Log.Connecting"] = new("Connecting to {0}...", "正在连接 {0}…"),
                ["Log.Connected"] = new("✓ Connected successfully - stream ready", "✓ 连接成功——视频流已就绪"),
                ["Log.ConnectionFailed"] = new("ERROR: Connection failed - check URL and credentials", "错误：连接失败——请检查 URL 和登录凭据"),
                ["Log.ConnectionError"] = new("ERROR: Connection error - {0}", "错误：连接故障——{0}"),
                ["Log.ReplicaConnecting"] = new("Connecting to the configured AI Weather primary...", "正在连接已配置的 AI Weather 主节点…"),
                ["Log.ReplicaConnected"] = new("✓ Primary synchronization started", "✓ 主节点同步已启动"),
                ["Log.ReplicaDisconnecting"] = new("Disconnecting from the AI Weather primary...", "正在断开 AI Weather 主节点…"),
                ["Log.ReplicaConnectionFailed"] = new("ERROR: Primary connection failed - check the primary URL, shared token, and Safety Monitor state", "错误：主节点连接失败——请检查主节点地址、共享令牌和安全监视器状态"),
                ["Log.StateRestored"] = new("✓ Monitoring state restored - monitoring is active", "✓ 已恢复监视状态——监视正在运行"),
                ["Log.MonitoringStarting"] = new("▶ Starting periodic monitoring...", "▶ 正在启动定期监视…"),
                ["Log.MonitoringStarted"] = new("✓ Monitoring started ({0} min intervals)", "✓ 监视已启动（间隔 {0} 分钟）"),
                ["Log.MonitoringActive"] = new("📊 Periodic monitoring active - next update in {0} minute(s)", "📊 定期监视运行中——{0} 分钟后进行下次更新"),
                ["Log.NoSource"] = new("ERROR: No camera source configured", "错误：未配置相机源"),
                ["Log.ConnectFailed"] = new("ERROR: Failed to connect", "错误：连接失败"),
                ["Log.RestartingStream"] = new("✓ Restarting RTSP video stream...", "✓ 正在重新启动 RTSP 视频流…"),
                ["Log.RestoringMode"] = new("✓ Restoring monitoring display for {0} mode...", "✓ 正在恢复 {0} 模式的监视画面…"),
                ["Log.ViewInitialized"] = new("✓ AI Weather Monitor view initialized", "✓ AI 天气监视器视图已初始化"),
                ["Log.NewSource"] = new("➕ New camera source added", "➕ 已添加相机源"),
                ["Log.DeleteRunning"] = new("⚠ Stop stream before deleting source {0}", "⚠ 请先停止视频流，再删除相机源 {0}"),
                ["Log.SourceRemoved"] = new("➖ Camera source removed: {0}", "➖ 已删除相机源：{0}"),
                ["Log.StoppingStream"] = new("⏹ Stopping stream from {0}...", "⏹ 正在停止视频流：{0}…"),
                ["Log.StreamStopped"] = new("✓ Stream stopped: {0}", "✓ 视频流已停止：{0}"),
                ["Log.MediaRequired"] = new("⚠ ERROR: Media URL is required for {0}", "⚠ 错误：{0} 必须填写媒体 URL"),
                ["Log.StartingStream"] = new("▶ Starting RTSP stream from {0}...", "▶ 正在启动 RTSP 视频流：{0}…"),
                ["Log.MissingPath"] = new("⚠ RTSP URL has no stream path. Live preview may still work, but AI frame capture often fails. Enter the full RTSP stream URL (include /stream or similar).", "⚠ RTSP URL 没有视频流路径。实时预览可能仍可工作，但 AI 往往无法截取帧。请输入完整的 RTSP 视频流 URL（包含 /stream 或类似路径）。"),
                ["Log.StreamStarted"] = new("✓ Live RTSP stream started: {0}", "✓ RTSP 实时视频流已启动：{0}"),
                ["Log.ConnectingAnalysis"] = new("📊 Connecting AI analysis for RTSP mode...", "📊 正在为 RTSP 模式连接 AI 分析…"),
                ["Log.AnalysisConnected"] = new("✓ AI analysis connected - automatic monitoring enabled", "✓ AI 分析已连接——已启用自动监视"),
                ["Log.AnalysisSchedule"] = new("📊 Weather analysis will run automatically every {0} minute(s)", "📊 天气分析将每 {0} 分钟自动运行一次"),
                ["Log.AnalysisNotConnected"] = new("⚠ AI analysis not connected (preview still running)", "⚠ AI 分析未连接（预览仍在运行）"),
                ["Log.VideoNotInitialized"] = new("❌ ERROR: Video view not initialized", "❌ 错误：视频视图尚未初始化"),
                ["Log.StreamError"] = new("❌ ERROR: Stream error - {0}", "❌ 错误：视频流故障——{0}"),
                ["Log.FrameQueued"] = new("✓ Current frame queued for dataset review", "✓ 当前帧已加入数据集复核队列"),
                ["Log.FrameNotQueued"] = new("⚠ Frame not queued (enable dataset collection and wait for an analysis first)", "⚠ 当前帧未加入队列（请启用数据集收集，并先等待一次分析）"),
                ["Log.QueueFailed"] = new("ERROR: Could not queue current frame for review - {0}", "错误：无法将当前帧加入复核队列——{0}"),
                ["Log.ReviewerOpened"] = new("✓ Dataset label reviewer opened", "✓ 已打开数据集标签检查器"),
                ["Log.ReviewerOpenFailed"] = new("ERROR: Could not open dataset label reviewer - {0}", "错误：无法打开数据集标签检查器——{0}"),
                ["Log.RefreshingPreview"] = new("Refreshing camera preview...", "正在刷新相机预览…"),
                ["Log.CameraNotConnected"] = new("ERROR: Not connected to camera", "错误：相机未连接"),
                ["Log.CaptureFailed"] = new("ERROR: Failed to capture frame", "错误：截取图像帧失败"),
                ["Log.CaptureTip"] = new("Tip: RTSP AI capture uses OpenCV/FFmpeg (not VLC). If your URL is just rtsp://IP it may show video but still return empty frames. Use the camera's full RTSP stream URL including the path (e.g. /stream, /live, /h264) and port if needed.", "提示：RTSP AI 截帧使用 OpenCV/FFmpeg（不是 VLC）。如果 URL 只有 rtsp://IP，可能可以播放视频却只能返回空帧。请使用相机完整的 RTSP 视频流 URL，包含路径（如 /stream、/live、/h264），必要时也要填写端口。"),
                ["Log.CaptureSuccess"] = new("✓ Frame captured successfully", "✓ 图像帧截取成功"),
                ["Log.Analysis"] = new("Analysis: {0} (Cloud: {1:F1}%)", "分析：{0}（云量：{1:F1}%）"),
                ["Log.Rain"] = new("⚠ Rain detected", "⚠ 检测到雨"),
                ["Log.Fog"] = new("⚠ Fog detected", "⚠ 检测到雾"),
                ["Log.Status"] = new("Status: {0}", "状态：{0}"),
                ["Log.ImageMissing"] = new("WARNING: Image captured but not found", "警告：已截取图像，但未找到图像文件"),
                ["Log.Error"] = new("ERROR: {0}", "错误：{0}"),
                ["Log.ImageSaved"] = new("✓ Image saved to {0}", "✓ 图像已保存到 {0}"),
                ["Log.ImageSaveFailed"] = new("ERROR: Failed to save image - {0}", "错误：保存图像失败——{0}"),

                ["Sequencer.Start"] = new("AI Weather Start", "启动 AI 天气监视"),
                ["Sequencer.Stop"] = new("AI Weather Stop", "停止 AI 天气监视"),
                ["Sequencer.Already"] = new("AI Weather: Already monitoring", "AI 天气：监视已在运行"),
                ["Sequencer.Starting"] = new("AI Weather: Starting monitoring...", "AI 天气：正在启动监视…"),
                ["Sequencer.Active"] = new("AI Weather: Monitoring active", "AI 天气：监视运行中"),
                ["Sequencer.StartFailed"] = new("AI Weather: Failed to start monitoring", "AI 天气：监视启动失败"),
                ["Sequencer.NotRunning"] = new("AI Weather: Not currently monitoring", "AI 天气：当前未在监视"),
                ["Sequencer.Stopped"] = new("AI Weather: Monitoring stopped", "AI 天气：监视已停止")
            };

        public static bool IsChineseCulture(CultureInfo? culture = null)
        {
            culture ??= CultureInfo.CurrentUICulture;
            return string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase);
        }

        public static string Text(string key, params object?[] args)
        {
            if (!Strings.TryGetValue(key, out var translation))
            {
                return $"[{key}]";
            }

            var value = IsChineseCulture() ? translation.Chinese : translation.English;
            return args.Length == 0 ? value : string.Format(CultureInfo.CurrentCulture, value, args);
        }

        public static string ReviewStatus(string status)
        {
            return status switch
            {
                DatasetReviewStatuses.Unreviewed => Text("Review.Unreviewed"),
                DatasetReviewStatuses.Accepted => Text("Review.Accepted"),
                DatasetReviewStatuses.Corrected => Text("Review.Corrected"),
                DatasetReviewStatuses.Rejected => Text("Review.Rejected"),
                _ => status
            };
        }

        public static string Condition(WeatherCondition condition)
        {
            if (!IsChineseCulture())
            {
                return condition.ToString();
            }

            return condition switch
            {
                WeatherCondition.Clear => "晴朗",
                WeatherCondition.PartlyCloudy => "局部多云",
                WeatherCondition.MostlyCloudy => "大部多云",
                WeatherCondition.Overcast => "阴天",
                WeatherCondition.Rainy => "降雨",
                WeatherCondition.Foggy => "有雾",
                _ => Text("Common.Unknown")
            };
        }

        public static string AnalysisDescription(WeatherAnalysisResult result, string? fallbackProvider = null)
        {
            var localDescription = result.RainDetected
                ? Text("Runtime.LocalRainDescription")
                : result.FogDetected
                    ? Text("Runtime.LocalFogDescription")
                    : Text("Runtime.LocalCloudDescription", Condition(result.Condition), result.CloudCoverage);

            if (result.Provenance.IsFallback)
            {
                var provider = !string.IsNullOrWhiteSpace(fallbackProvider)
                    && !string.Equals(fallbackProvider, "Local", StringComparison.OrdinalIgnoreCase)
                        ? fallbackProvider
                        : string.IsNullOrWhiteSpace(result.Provenance.Provider)
                            ? Text("Runtime.OnlineTeacher")
                            : result.Provenance.Provider;
                provider = FriendlyRuntimeProviderName(provider);

                if (IsFreePoolFailure(result.Provenance))
                {
                    return FreePoolFailureSummary(result.Provenance) + " " + localDescription;
                }

                if (result.Provenance.FailureCategory == AnalysisFailureCategory.QuotaExhausted)
                {
                    if (IsDailyQuota(result.Provenance)
                        && result.Provenance.RetryAfterUtc is DateTime dailyRetryAfterUtc)
                    {
                        return Text(
                            "Runtime.FallbackDailyQuotaDescription",
                            provider,
                            FormatRetryAfter(dailyRetryAfterUtc),
                            localDescription);
                    }

                    return result.Provenance.RetryAfterUtc is DateTime retryAfterUtc
                        ? Text(
                            "Runtime.FallbackQuotaDescription",
                            provider,
                            FormatRetryAfter(retryAfterUtc),
                            localDescription)
                        : Text(
                            "Runtime.FallbackQuotaDescriptionNoTime",
                            provider,
                            localDescription);
                }

                if (result.Provenance.FailureCategory == AnalysisFailureCategory.ScheduledLocal)
                {
                    return Text(
                        "Runtime.ScheduledLocalDescription",
                        provider,
                        Math.Max(1, result.Provenance.RequestEveryChecks),
                        localDescription);
                }

                return Text(
                    "Runtime.FallbackDescription",
                    provider,
                    FailureCategory(result.Provenance),
                    localDescription);
            }

            return result.Provenance.Origin is AnalysisOrigin.LocalHeuristic or AnalysisOrigin.LocalOnnx
                ? localDescription
                : result.Description;
        }

        private static bool IsFreePoolFailure(AnalysisProvenance provenance) =>
            provenance.ProviderFailureCode is "free_pool_exhausted" or "free_pool_daily_quota";

        public static string FreePoolFailureSummary(AnalysisProvenance provenance)
        {
            var entries = provenance.AttemptDiagnostics
                .GroupBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .Select(item => item.Model + ": " + FailureCategory(new AnalysisProvenance
                {
                    FailureCategory = item.FailureCategory,
                    HttpStatus = item.HttpStatus
                }));
            var summary = Text("Runtime.FreePoolFailed") + " " + string.Join("; ", entries);
            if (provenance.RetryAfterUtc is DateTime retry)
                summary += " " + Text("Runtime.FreePoolRetry", FormatRetryAfter(retry));
            return summary;
        }

        private static string FriendlyRuntimeProviderName(string provider)
        {
            if (GeminiProviderProfile.IsPaid(provider))
            {
                return Text("Runtime.ProviderNameGeminiPaid");
            }

            if (GeminiProviderProfile.IsFree(provider))
            {
                return Text("Runtime.ProviderNameGeminiFree");
            }

            return provider;
        }

        public static string FallbackStatus(AnalysisProvenance provenance)
        {
            if (IsFreePoolFailure(provenance)) return FreePoolFailureSummary(provenance);
            if (provenance.FailureCategory == AnalysisFailureCategory.QuotaExhausted)
            {
                if (IsDailyQuota(provenance)
                    && provenance.RetryAfterUtc is DateTime dailyRetryAfterUtc)
                {
                    return Text("Runtime.FallbackDailyQuota", FormatRetryAfter(dailyRetryAfterUtc));
                }

                return provenance.RetryAfterUtc is DateTime retryAfterUtc
                    ? Text("Runtime.FallbackQuota", FormatRetryAfter(retryAfterUtc))
                    : Text("Runtime.FallbackQuotaNoTime");
            }

            if (provenance.FailureCategory == AnalysisFailureCategory.ScheduledLocal)
            {
                return Text(
                    "Runtime.ScheduledLocal",
                    Math.Max(1, provenance.RequestEveryChecks));
            }

            return Text("Runtime.Fallback", FailureCategory(provenance));
        }

        public static string FailureCategory(AnalysisProvenance provenance)
        {
            var category = FailureCategory(provenance.FailureCategory);
            if (provenance.HttpStatus is not int httpStatus)
            {
                return category;
            }

            return IsChineseCulture()
                ? $"{category}，HTTP {httpStatus}"
                : $"{category}, HTTP {httpStatus}";
        }

        public static string FailureCategory(AnalysisFailureCategory category)
        {
            if (!IsChineseCulture())
            {
                return category switch
                {
                    AnalysisFailureCategory.QuotaExhausted => "API quota temporarily unavailable",
                    AnalysisFailureCategory.ScheduledLocal => "scheduled local check",
                    AnalysisFailureCategory.ServiceUnavailable => "service temporarily unavailable",
                    _ => category.ToString()
                };
            }

            return category switch
            {
                AnalysisFailureCategory.None => "无",
                AnalysisFailureCategory.RateLimited => "达到频率限制",
                AnalysisFailureCategory.Timeout => "超时",
                AnalysisFailureCategory.Network => "网络错误",
                AnalysisFailureCategory.Authentication => "身份验证失败",
                AnalysisFailureCategory.ModelUnavailable => "模型不可用",
                AnalysisFailureCategory.MalformedResponse => "响应格式错误",
                AnalysisFailureCategory.SchemaRejected => "响应结构不合格",
                AnalysisFailureCategory.Cancelled => "已取消",
                AnalysisFailureCategory.ServiceRetired => "服务已停止",
                AnalysisFailureCategory.QuotaExhausted => "API 配额暂不可用",
                AnalysisFailureCategory.ScheduledLocal => "按计划使用本地检查",
                AnalysisFailureCategory.ServiceUnavailable => "服务暂不可用",
                _ => "未知错误"
            };
        }

        private static string FormatRetryAfter(DateTime retryAfterUtc)
        {
            var normalizedUtc = retryAfterUtc.Kind == DateTimeKind.Utc
                ? retryAfterUtc
                : DateTime.SpecifyKind(retryAfterUtc, DateTimeKind.Utc);
            var local = normalizedUtc.ToLocalTime();
            var format = local.Date == DateTime.Now.Date
                ? "HH:mm:ss"
                : "yyyy-MM-dd HH:mm:ss";
            var localText = local.ToString(format, CultureInfo.CurrentCulture);
            var utcOffset = TimeZoneInfo.Local.GetUtcOffset(local);
            var offsetText = utcOffset < TimeSpan.Zero
                ? $"-{utcOffset.Duration():hh\\:mm}"
                : $"+{utcOffset:hh\\:mm}";
            return IsChineseCulture()
                ? $"{localText}（本地时间 UTC{offsetText}）"
                : $"{localText} local time (UTC{offsetText})";
        }

        private static bool IsDailyQuota(AnalysisProvenance provenance)
        {
            return !string.IsNullOrWhiteSpace(provenance.QuotaId)
                   && (provenance.QuotaId.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
                       || provenance.QuotaId.Contains("RequestsPerDay", StringComparison.OrdinalIgnoreCase));
        }

        public static string Boolean(bool value) => Text(value ? "Common.True" : "Common.False");

        public static string SelectionReason(string reason)
        {
            if (!IsChineseCulture())
            {
                return reason;
            }

            return reason switch
            {
                "manualReview" => "手动保留",
                "initial" => "初始样本",
                "conditionChanged" => "天气类别变化",
                "thresholdCrossing" => "跨越云量阈值",
                "periodic" => "定期采样",
                "teacherStudentDisagreement" => "教师/学生云量分歧",
                "teacherStudentSafetyDisagreement" => "教师/学生安全判断分歧",
                "teacherLowConfidence" => "教师置信度低",
                "sunStateChanged" => "太阳状态变化",
                "effectiveSafetyChanged" => "最终安全状态变化",
                "visualSafetyChanged" => "视觉安全状态变化",
                "externalSafetyChanged" => "外部安全状态变化",
                "teacherInconsistent" => "教师结果不一致",
                "teacherInvalidResponse" => "教师响应无效",
                "teacherUnavailable" => "教师不可用",
                _ => reason
            };
        }

        public static string AstroTerm(string value)
        {
            if (!IsChineseCulture())
            {
                return value;
            }

            return value switch
            {
                "Day" => "白天",
                "CivilTwilight" => "民用曙暮光",
                "NauticalTwilight" => "航海曙暮光",
                "AstronomicalTwilight" => "天文曙暮光",
                "Night" => "夜晚",
                "New Moon" => "新月",
                "Waxing Crescent" => "娥眉月",
                "First Quarter" => "上弦月",
                "Waxing Gibbous" => "盈凸月",
                "Full Moon" => "满月",
                "Waning Gibbous" => "亏凸月",
                "Last Quarter" => "下弦月",
                "Waning Crescent" => "残月",
                _ => value
            };
        }
    }

    /// <summary>Usage: Text="{i18n:Loc Preview.ActivityLog}".</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension(string key)
        {
            Key = key;
        }

        [ConstructorArgument("key")]
        public string Key { get; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            return UiLocalization.Text(Key);
        }
    }

    public sealed class ReviewStatusLocalizationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var status = value as string;
            return string.Equals(status, "All", StringComparison.Ordinal)
                ? UiLocalization.Text("Review.FilterAll")
                : UiLocalization.ReviewStatus(status ?? string.Empty);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    public sealed class WeatherConditionLocalizationConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is WeatherCondition condition
                ? UiLocalization.Condition(condition)
                : UiLocalization.Text("Common.Unknown");
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
