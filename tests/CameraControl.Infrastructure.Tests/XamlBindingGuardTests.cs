using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace CameraControl.Infrastructure.Tests;

/// <summary>
/// XAML 绑定约束的静态检查（构建期回归，不依赖 WPF 运行时）。
///
/// 背景：<c>Run.Text</c> 的绑定默认模式是 **TwoWay**，
/// 一旦绑定到只读属性，运行期每渲染一行就抛一次异常。
/// 曾导致"点刷新后弹出一叠错误框、随后闪退"。
///
/// 这类错误编译器不会报、单元测试也碰不到（要真跑 UI），
/// 所以用静态扫描把规则固定下来。
/// </summary>
public sealed class XamlBindingGuardTests
{
    [Fact]
    public void Run的Text不得绑定到只读属性()
    {
        string root = FindRepositoryRoot();

        List<string> offenders = [];

        foreach (string file in Directory.EnumerateFiles(
                     Path.Combine(root, "src"),
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);

            // 只检查 <Run Text="{Binding ...}" />：TextBlock.Text 默认 OneWay，是安全的
            foreach (Match match in Regex.Matches(text, @"<Run\s+Text=""\{Binding[^}]*\}"""))
            {
                offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Run.Text 绑定默认 TwoWay，绑只读属性会在运行期抛异常。改成单个只读属性 + TextBlock.Text：\n"
            + string.Join("\n", offenders));
    }

    /// <summary>从测试输出目录向上找到仓库根（含 CameraControl.slnx）。</summary>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CameraControl.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未能定位仓库根目录（找不到 CameraControl.slnx）。");
    }
}
