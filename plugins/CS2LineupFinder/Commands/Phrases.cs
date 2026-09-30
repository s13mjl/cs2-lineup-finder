using System;
using CS2LineupFinder.Plugin.State;

namespace CS2LineupFinder.Plugin.Commands;

/// <summary>
/// Player-facing text. Every command answers in the language configured in
/// <c>config.toml</c>; the two dictionaries are keyed identically so a missing
/// translation falls back to English rather than printing a key.
/// </summary>
public static class Phrases
{
    private static readonly string[] En =
    {
        /* 00 */ "[LineupFinder] {0}",
        /* 01 */ "Marked throw point at {0}.",
        /* 02 */ "Landing zone: {0}.",
        /* 03 */ "Grenade set to {0}.",
        /* 04 */ "Throw mode set to {0}.",
        /* 05 */ "Mouse button set to {0}.",
        /* 06 */ "Searching {0} line-up...",
        /* 07 */ "Found {0} line-up(s) in {1:0.0}s (solver: {2}).",
        /* 08 */ "  #{0}  yaw {1:0.0}  pitch {2:0.0}  miss {3:0.0}u",
        /* 09 */ "No line-up found: {0}",
        /* 10 */ "Search failed ({0}): {1}",
        /* 11 */ "Search timed out after {0:0.0}s.",
        /* 12 */ "Saved line-up '{0}' to {1}.",
        /* 13 */ "Line-up '{0}' not found on this map.",
        /* 14 */ "Loaded line-up '{0}': yaw {1:0.0} pitch {2:0.0}.",
        /* 15 */ "{0} line-up(s) saved on {1}.",
        /* 16 */ "  {0}  ({1})",
        /* 17 */ "Drawing the landing zone for {0}.",
        /* 18 */ "Set a throw point first: css_lf_start.",
        /* 19 */ "Set a landing zone first: css_lf_zone circle <radius>.",
        /* 20 */ "Both throw point and landing zone are required.",
        /* 21 */ "Unknown option '{0}'. Usage: {1}",
        /* 22 */ "Config reloaded: {0}",
        /* 23 */ "A search is already running.",
        /* 24 */ "Selection cleared.",
        /* 25 */ "Visuals are disabled in config.toml.",
        /* 26 */ "Simulator is the built-in stub. Install the Core plugin for real physics.",
        /* 27 */ "Radius must be a positive number.",
        /* 28 */ "Width and height must be positive numbers.",
        /* 29 */ "Beam drawn for {0:0.0}s.",
        /* 30 */ "Unknown command. Try !lf_menu.",
        /* 31 */ "Candidates: {0}, budget {1:0.0}s, results {2}.",
        /* 32 */ "Invalid name: use letters, digits, '-' or '_'.",
        /* 33 */ "Nothing to save yet: run css_lf_find first.",
        /* 34 */ "This server cannot trace the map, so the zone was placed {0}u along your view.",
        /* 35 */ "Could not write the line-up: {0}",
        /* 36 */ "Could not read the line-up: {0}",
        /* 37 */ "You need to be alive to use that command.",
        /* 38 */ "Line-up finder - {0}",
        /* 39 */ "Mark the throw point here (standing)",
        /* 40 */ "Mark the throw point here (crouched)",
        /* 41 */ "Mark the throw point here (jumping)",
        /* 42 */ "Grenade: {0}   (click to cycle)",
        /* 43 */ "Throw: {0}   (click to cycle)",
        /* 44 */ "Button: {0}   (click to cycle)",
        /* 45 */ "Search for a line-up",
        /* 46 */ "Save the best result:  css_lf_save <name>",
        /* 47 */ "List the line-ups saved on this map",
        /* 48 */ "Redraw the aim beam and the impact mark",
        /* 49 */ "Clear the throw point and the landing zone",
        /* 50 */ "Reload config.toml",
        /* 51 */ "Pick another result:  css_lf_set <index>",
        /* 52 */ "The line-up finder is disabled on {0}.",
        /* 53 */ "Saved under {0}",
        /* 54 */ "This server cannot trace the map, so zones fall back to a fixed distance.",
    };

    private static readonly string[] Zh =
    {
        /* 00 */ "[瞄点预测] {0}",
        /* 01 */ "已标记投掷点：{0}。",
        /* 02 */ "落点范围：{0}。",
        /* 03 */ "道具已设为 {0}。",
        /* 04 */ "投掷方式已设为 {0}。",
        /* 05 */ "鼠标按键已设为 {0}。",
        /* 06 */ "正在搜索 {0} 瞄点……",
        /* 07 */ "找到 {0} 个瞄点，用时 {1:0.0} 秒（求解器：{2}）。",
        /* 08 */ "  #{0}  水平 {1:0.0}  俯仰 {2:0.0}  偏差 {3:0.0}u",
        /* 09 */ "未找到瞄点：{0}",
        /* 10 */ "搜索失败（{0}）：{1}",
        /* 11 */ "搜索在 {0:0.0} 秒后超时。",
        /* 12 */ "已保存瞄点「{0}」到 {1}。",
        /* 13 */ "本地图上没有瞄点「{0}」。",
        /* 14 */ "已载入瞄点「{0}」：水平 {1:0.0} 俯仰 {2:0.0}。",
        /* 15 */ "{1} 上共有 {0} 个已保存瞄点。",
        /* 16 */ "  {0}  ({1})",
        /* 17 */ "正在绘制 {0} 的落点范围。",
        /* 18 */ "请先标记投掷点：css_lf_start。",
        /* 19 */ "请先创建落点范围：css_lf_zone circle <半径>。",
        /* 20 */ "需要同时具备投掷点与落点范围。",
        /* 21 */ "未知参数「{0}」。用法：{1}",
        /* 22 */ "配置已重新载入：{0}",
        /* 23 */ "已有搜索正在进行。",
        /* 24 */ "已清除选择。",
        /* 25 */ "可视化在 config.toml 中已关闭。",
        /* 26 */ "当前使用内置桩模拟器。安装 Core 插件后才会使用真实物理。",
        /* 27 */ "半径必须为正数。",
        /* 28 */ "长和宽必须为正数。",
        /* 29 */ "光束显示 {0:0.0} 秒。",
        /* 30 */ "未知指令。请尝试 !lf_menu。",
        /* 31 */ "候选 {0} 个，预算 {1:0.0} 秒，结果 {2} 条。",
        /* 32 */ "名称无效：请使用字母、数字、'-' 或 '_'。",
        /* 33 */ "暂无可保存内容：请先执行 css_lf_find。",
        /* 34 */ "本服务器无法进行地图射线检测，落点范围已放置在你视线前方 {0}u 处。",
        /* 35 */ "无法写入瞄点文件：{0}",
        /* 36 */ "无法读取瞄点文件：{0}",
        /* 37 */ "该指令需要你处于存活状态。",
        /* 38 */ "瞄点预测 - {0}",
        /* 39 */ "在当前位置标记投掷点（站立）",
        /* 40 */ "在当前位置标记投掷点（下蹲）",
        /* 41 */ "在当前位置标记投掷点（跳跃）",
        /* 42 */ "道具：{0}（点击切换）",
        /* 43 */ "投掷方式：{0}（点击切换）",
        /* 44 */ "鼠标按键：{0}（点击切换）",
        /* 45 */ "搜索瞄点",
        /* 46 */ "保存最佳结果：css_lf_save <名称>",
        /* 47 */ "列出本地图已保存的瞄点",
        /* 48 */ "重新绘制瞄准光束与落点标记",
        /* 49 */ "清除投掷点与落点范围",
        /* 50 */ "重新载入 config.toml",
        /* 51 */ "切换其它结果：css_lf_set <序号>",
        /* 52 */ "瞄点预测在 {0} 上未启用。",
        /* 53 */ "保存位置：{0}",
        /* 54 */ "本服务器无法进行地图射线检测，落点将使用固定距离回退。",
    };

    /// <summary>Index of the message wrapped with the plugin prefix.</summary>
    private const int PrefixFormat = 0;

    /// <summary>Named indices into the message tables, so call sites never use bare numbers.</summary>
    public static class Id
    {
        /// <summary>The throw point was marked.</summary>
        public const int MarkedThrowPoint = 1;

        /// <summary>The landing zone was created or changed.</summary>
        public const int LandingZone = 2;

        /// <summary>The grenade family changed.</summary>
        public const int GrenadeSet = 3;

        /// <summary>The throw stance changed.</summary>
        public const int ThrowModeSet = 4;

        /// <summary>The mouse button changed.</summary>
        public const int ButtonSet = 5;

        /// <summary>A search has started.</summary>
        public const int Searching = 6;

        /// <summary>A search produced results.</summary>
        public const int Found = 7;

        /// <summary>One result line.</summary>
        public const int ResultLine = 8;

        /// <summary>A search finished without a solution.</summary>
        public const int NoSolution = 9;

        /// <summary>A search failed.</summary>
        public const int SearchFailed = 10;

        /// <summary>A search ran out of time.</summary>
        public const int TimedOut = 11;

        /// <summary>A line-up was written to disk.</summary>
        public const int Saved = 12;

        /// <summary>A named line-up does not exist on this map.</summary>
        public const int NotFound = 13;

        /// <summary>A line-up was loaded.</summary>
        public const int Loaded = 14;

        /// <summary>Header of <c>css_lf_list</c>.</summary>
        public const int ListHeader = 15;

        /// <summary>One entry of <c>css_lf_list</c>.</summary>
        public const int ListEntry = 16;

        /// <summary>The landing zone is being drawn.</summary>
        public const int DrawingZone = 17;

        /// <summary>No throw point has been marked yet.</summary>
        public const int NeedThrowPoint = 18;

        /// <summary>No landing zone has been created yet.</summary>
        public const int NeedZone = 19;

        /// <summary>Both a throw point and a landing zone are required.</summary>
        public const int NeedBoth = 20;

        /// <summary>An argument was not understood.</summary>
        public const int UnknownOption = 21;

        /// <summary>The configuration was reloaded.</summary>
        public const int ConfigReloaded = 22;

        /// <summary>A search is already running for this player.</summary>
        public const int SearchRunning = 23;

        /// <summary>The marked throw point and landing zone were cleared.</summary>
        public const int Cleared = 24;

        /// <summary>Visualization is switched off in the configuration.</summary>
        public const int VisualsDisabled = 25;

        /// <summary>The built-in stand-in simulator is in use.</summary>
        public const int StubSimulator = 26;

        /// <summary>A radius argument was not a positive number.</summary>
        public const int BadRadius = 27;

        /// <summary>A width or height argument was not a positive number.</summary>
        public const int BadExtent = 28;

        /// <summary>The aim beam was drawn.</summary>
        public const int BeamDrawn = 29;

        /// <summary>The command was not recognized.</summary>
        public const int UnknownCommand = 30;

        /// <summary>Search settings summary.</summary>
        public const int SearchSettings = 31;

        /// <summary>A line-up name is not usable.</summary>
        public const int BadName = 32;

        /// <summary>There is no search result to save.</summary>
        public const int NothingToSave = 33;

        /// <summary>The zone was placed without a trace, at the fallback distance.</summary>
        public const int ZoneNotTraced = 34;

        /// <summary>Writing a line-up failed.</summary>
        public const int SaveFailed = 35;

        /// <summary>Reading a line-up failed.</summary>
        public const int LoadFailed = 36;

        /// <summary>Reported when a command needs a live player and there is none.</summary>
        public const int NoPawn = 37;

        /// <summary>Title of the main menu.</summary>
        public const int MenuTitle = 38;

        /// <summary>Menu entry: mark the throw point standing.</summary>
        public const int MenuMarkStand = 39;

        /// <summary>Menu entry: mark the throw point crouched.</summary>
        public const int MenuMarkCrouch = 40;

        /// <summary>Menu entry: mark the throw point jumping.</summary>
        public const int MenuMarkJump = 41;

        /// <summary>Menu entry showing the selected grenade.</summary>
        public const int MenuGrenade = 42;

        /// <summary>Menu entry showing the selected stance.</summary>
        public const int MenuThrow = 43;

        /// <summary>Menu entry showing the selected button.</summary>
        public const int MenuButton = 44;

        /// <summary>Menu entry: run a search.</summary>
        public const int MenuFind = 45;

        /// <summary>Menu entry: how to save.</summary>
        public const int MenuSave = 46;

        /// <summary>Menu entry: list saved line-ups.</summary>
        public const int MenuList = 47;

        /// <summary>Menu entry: redraw the last result.</summary>
        public const int MenuDraw = 48;

        /// <summary>Menu entry: clear the session.</summary>
        public const int MenuClear = 49;

        /// <summary>Menu entry: reload the configuration.</summary>
        public const int MenuReload = 50;

        /// <summary>Menu entry: how to pick another result.</summary>
        public const int MenuSet = 51;

        /// <summary>Reported when the map is not on the configured map list.</summary>
        public const int MapDisabled = 52;

        /// <summary>Directory a map's saved line-ups live in.</summary>
        public const int MapDirectory = 53;

        /// <summary>Reported when the trace backend cannot reach map geometry.</summary>
        public const int NoTraceBackend = 54;
    }

    /// <summary>Formats a message for the configured language.</summary>
    /// <param name="language">Either <c>en</c> or <c>zh-CN</c>.</param>
    /// <param name="id">Message index, see the <see cref="Phrases"/> constants.</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>The formatted, localized text.</returns>
    public static string Get(string language, int id, params object?[] args)
    {
        var table = IsChinese(language) ? Zh : En;
        var template = id >= 0 && id < table.Length ? table[id] : En[id];
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, args);
    }

    /// <summary>Formats a message and wraps it in the plugin prefix.</summary>
    /// <param name="language">Either <c>en</c> or <c>zh-CN</c>.</param>
    /// <param name="id">Message index, see the <see cref="Phrases"/> constants.</param>
    /// <param name="args">Format arguments.</param>
    /// <returns>The formatted, prefixed text.</returns>
    public static string Prefixed(string language, int id, params object?[] args)
        => string.Format(System.Globalization.CultureInfo.InvariantCulture, Get(language, PrefixFormat), Get(language, id, args));

    /// <summary>True when the configured language is Chinese.</summary>
    /// <param name="language">Configured language tag.</param>
    /// <returns><see langword="true"/> for <c>zh-CN</c>.</returns>
    public static bool IsChinese(string? language) => language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Describes a landing zone in player terms.</summary>
    /// <param name="zone">Zone to describe.</param>
    /// <param name="language">Configured language.</param>
    /// <returns>A short human readable description.</returns>
    public static string DescribeZone(Contracts.GroundZone zone, string language)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var centre = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({zone.Center.X:0}, {zone.Center.Y:0}, {zone.Center.Z:0})");
        return zone.Type == Contracts.GroundZoneType.Circle
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"circle r={zone.Radius:0}u @ {centre}")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"rect {zone.Width:0}x{zone.Height:0}u @ {centre}");
    }

    /// <summary>Describes what a player has configured so far, for the menu header.</summary>
    /// <param name="session">Player session.</param>
    /// <param name="language">Configured language.</param>
    /// <returns>A one-line status summary.</returns>
    public static string DescribeSession(PlayerSession session, string language)
    {
        ArgumentNullException.ThrowIfNull(session);

        var throwPoint = session.ThrowPoint.HasValue ? "set" : "unset";
        var zone = session.Zone is null ? "unset" : DescribeZone(session.Zone, language);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"origin {throwPoint} | zone {zone} | {CommandUsage.Token(session.GrenadeType)} | {CommandUsage.Token(session.ThrowMode)} | {CommandUsage.Token(session.Button)}");
    }
}
