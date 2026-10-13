using System.Windows;
using System.Windows.Controls;

namespace TcpRouteTray;

internal static class MainWindowLocalization
{
    private static readonly IReadOnlyDictionary<string, (string English, string Traditional)> Strings =
        new Dictionary<string, (string, string)>
        {
            ["概览"] = ("Overview", "概覽"), ["上游"] = ("Upstreams", "上游"), ["分流"] = ("Routing", "分流"),
            ["Host 来源"] = ("Host sources", "Host 來源"), ["活动"] = ("Activity", "活動"), ["设置"] = ("Settings", "設置"),
            ["当前状态"] = ("Current status", "當前狀態"), ["已停止"] = ("Stopped", "已停止"), ["已保存"] = ("Saved", "已保存"),
            ["系统代理"] = ("System proxy", "系統代理"), ["保存配置"] = ("Save configuration", "保存配置"), ["启动"] = ("Start", "啟動"),
            ["选择配置"] = ("Select profile", "選擇配置"), ["配置工具"] = ("Configuration tools", "配置工具"),
            ["新建配置…"] = ("New profile...", "新建配置…"), ["放弃草稿并重新载入"] = ("Discard draft and reload", "放棄草稿並重新載入"),
            ["导入文件…"] = ("Import file...", "導入文件…"), ["导入 URL…"] = ("Import URL...", "導入 URL…"),
            ["导出已保存配置…"] = ("Export saved configuration...", "導出已保存配置…"),
            ["TcpRoute2 已停止"] = ("TcpRoute2 is stopped", "TcpRoute2 已停止"), ["SOCKS5 服务未运行"] = ("SOCKS5 service is not running", "SOCKS5 服務未運行"),
            ["连通性检查"] = ("Connectivity check", "連通性檢查"), ["当前配置"] = ("Current profile", "當前配置"),
            ["显式保存的运行配置"] = ("Explicitly saved runtime profile", "顯式保存的運行配置"),
            ["Windows 代理"] = ("Windows proxy", "Windows 代理"), ["未启用"] = ("Disabled", "未啟用"),
            ["当前用户 SOCKS 设置"] = ("Current user SOCKS setting", "當前用戶 SOCKS 設置"),
            ["LAN 客户端"] = ("LAN clients", "LAN 客戶端"), ["未开放"] = ("Closed", "未開放"), ["默认仅监听本机"] = ("Listens locally by default", "默認僅監聽本機"),
            ["运行信息"] = ("Runtime", "運行信息"), ["核心进程"] = ("Core process", "核心進程"), ["等待启动"] = ("Waiting to start", "等待啟動"),
            ["上游数量"] = ("Upstreams", "上游數量"), ["最近事件"] = ("Recent event", "最近事件"),
            ["TcpRoute2 已就绪"] = ("TcpRoute2 is ready", "TcpRoute2 已就緒"), ["尚未执行连通性检查"] = ("Connectivity has not been checked", "尚未執行連通性檢查"),
            ["添加代理"] = ("Add proxy", "添加代理"), ["代理节点"] = ("Proxy nodes", "代理節點"), ["已配置"] = ("Configured", "已配置"),
            ["移除所选代理"] = ("Remove selected proxy", "移除所選代理"), ["节点详情"] = ("Node details", "節點詳情"),
            ["名称"] = ("Name", "名稱"), ["本地解析目标域名"] = ("Resolve destination locally", "本地解析目標域名"),
            ["开启：命中 hosts 时只使用其地址，未命中才查询 DNS。关闭：将域名交给上游代理解析。"] = ("On: matching hosts addresses override DNS; only misses query DNS. Off: the upstream proxy resolves domains.", "開啟：命中 hosts 時只使用其位址，未命中才查詢 DNS。關閉：將域名交給上游代理解析。"),
            ["完整代理 URL"] = ("Complete proxy URL", "完整代理 URL"),
            ["Direct 是内置上游，不会出现在这里。可在分流页为 Direct 配置规则。"] = ("Direct is built in and is configured on the Routing page.", "Direct 是內置上游，不會出現在這裡。可在分流頁為 Direct 配置規則。"),
            ["选择上游"] = ("Select upstream", "選擇上游"), ["优先规则（Whitelist）"] = ("Preferred rules (Whitelist)", "優先規則（Whitelist）"),
            ["排除规则（Blacklist）"] = ("Excluded rules (Blacklist)", "排除規則（Blacklist）"), ["本地文件"] = ("Local file", "本地文件"),
            ["远程文件"] = ("Remote file", "遠程文件"), ["选择文件…"] = ("Choose file...", "選擇文件…"), ["移除"] = ("Remove", "移除"),
            ["来源"] = ("Source", "來源"), ["匹配 / Geosite 名称"] = ("Match / Geosite names", "匹配 / Geosite 名稱"), ["更新周期"] = ("Update interval", "更新週期"),
            ["本地解析时，hosts 命中覆盖 DNS。"] = ("Matching hosts entries override DNS during local resolution.", "本機解析時，hosts 命中覆蓋 DNS。"),
            ["添加本地来源"] = ("Add local source", "添加本地來源"), ["添加远程来源"] = ("Add remote source", "添加遠程來源"), ["匹配"] = ("Match", "匹配"),
            ["显示 TcpRoute2 核心最近输出，不会修改日志文件。"] = ("Shows recent TcpRoute2 core output without modifying the log.", "顯示 TcpRoute2 核心最近輸出，不會修改日誌文件。"),
            ["刷新"] = ("Refresh", "刷新"), ["导出日志"] = ("Export log", "匯出日誌"), ["打开日志目录"] = ("Export log", "匯出日誌"),
            ["外观"] = ("Appearance", "外觀"), ["默认跟随 Windows；Windows 11 使用系统 Mica 背景。"] = ("Follows Windows by default; Windows 11 uses the system Mica backdrop.", "默認跟隨 Windows；Windows 11 使用系統 Mica 背景。"),
            ["语言"] = ("Language", "語言"), ["默认按 Windows 显示语言自动选择。"] = ("Uses the Windows display language by default.", "默認按 Windows 顯示語言自動選擇。"),
            ["允许 LAN 客户端"] = ("Allow LAN clients", "允許 LAN 客戶端"), ["启用"] = ("Enable", "啟用"),
            ["运行时将监听地址改写为 0.0.0.0；配置文件本身保持不变。"] = ("Uses 0.0.0.0 at runtime without changing the saved configuration.", "運行時將監聽地址改寫為 0.0.0.0；配置文件本身保持不變。"),
            ["允许 STUN"] = ("Allow STUN", "允許 STUN"), ["允许 STUN 数据和常用发现端口；默认关闭。"] = ("Allow STUN payloads and common discovery ports. Disabled by default.", "允許 STUN 資料和常用探索連接埠；預設關閉。"),
            ["允许 QUIC"] = ("Allow QUIC", "允許 QUIC"), ["允许 QUIC 使用 UDP 443；默认关闭。"] = ("Allow UDP port 443 for QUIC. Disabled by default.", "允許 QUIC 使用 UDP 443；預設關閉。"),
            ["启用 IPv6"] = ("Enable IPv6", "啟用 IPv6"), ["允许本地可识别的 IPv6 目标和 DNS 候选；默认关闭。"] = ("Allow locally known IPv6 targets and DNS candidates. Disabled by default.", "允許本機可識別的 IPv6 目標和 DNS 候選；預設關閉。"),
            ["TcpRoute2 核心程序"] = ("TcpRoute2 core executable", "TcpRoute2 核心程序"), ["自动查找失败时，可手动指定 TcpRoute2.exe。"] = ("Select TcpRoute2.exe when automatic discovery fails.", "自動查找失敗時，可手動指定 TcpRoute2.exe。"),
            ["选择程序…"] = ("Choose executable...", "選擇程序…"), ["配置与高级编辑"] = ("Configuration and advanced editing", "配置與高級編輯"),
            ["Quick UI 保留已有监听端口但不提供编辑；需要修改完整语义时使用 Advanced TOML。"] = ("Quick UI preserves the listen port. Use Advanced TOML for complete editing.", "Quick UI 保留已有監聽端口但不提供編輯；需要修改完整語義時使用 Advanced TOML。"),
            ["配置目录"] = ("Configuration folder", "配置目錄")
        };

    public static void Apply(DependencyObject root, string preference)
    {
        TranslateObject(root, preference);
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) Apply(child, preference);
    }

    private static void TranslateObject(DependencyObject value, string preference)
    {
        if (value is TextBlock text && !string.IsNullOrEmpty(text.Text)) text.Text = Translate(text.Text, preference);
        if (value is ContentControl content && content.Content is string contentText) content.Content = Translate(contentText, preference);
        if (value is HeaderedContentControl header && header.Header is string headerText) header.Header = Translate(headerText, preference);
        if (value is HeaderedItemsControl items && items.Header is string itemsHeader) items.Header = Translate(itemsHeader, preference);
        if (value is FrameworkElement element && element.ToolTip is string toolTip) element.ToolTip = Translate(toolTip, preference);
        if (value is DataGrid grid)
            foreach (var column in grid.Columns)
                if (column.Header is string columnHeader) column.Header = Translate(columnHeader, preference);
    }

    private static string Translate(string value, string preference)
    {
        var resolved = WindowsLocalization.Resolve(preference);
        foreach (var (simplified, translations) in Strings)
        {
            if (value != simplified && value != translations.English && value != translations.Traditional) continue;
            return resolved switch { "zh-Hans" => simplified, "zh-Hant" => translations.Traditional, _ => translations.English };
        }
        return value;
    }
}
