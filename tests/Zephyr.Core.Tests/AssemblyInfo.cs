using Xunit;

// AppSettings 的读写路径不可注入 (见 AppSettingsTests 的说明), 测试只能操作真实的
// %LOCALAPPDATA%\Zephyr\settings.json; 同时 AppSettings.LastError / LoadDegraded /
// LoadWarning 是进程级静态字段。两者都不能被并行执行安全共享, 所以整个程序集关掉并行。
//
// 代价是跑得慢一点 (本工程几十个测试, 实测无感), 换来的是"测试永远不会因为邻居
// 写了同一个文件而随机变红" —— 一个会随机失败的回归网比没有回归网更糟。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
