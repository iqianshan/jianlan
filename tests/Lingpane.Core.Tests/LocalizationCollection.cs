using Xunit;

namespace Lingpane.Core.Tests;

/// <summary>
/// 改 <see cref="LocalizationService"/> 单例（会改进程级 CultureInfo）的测试都归到这个集合，
/// 集合内串行、且与其他集合不并行。
///
/// 不这样做的后果实测过：两个测试类并行时，一个刚切到中文、另一个在读它之前被切回英文，
/// 断言会以随机顺序失败——而失败信息看起来像"文案没走资源键"，很容易误判成产品缺陷。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalizationCollection
{
    public const string Name = "Localization";
}
