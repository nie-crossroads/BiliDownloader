// net48 编译器补给：C# 9~11 的 record / init / required 语法糖依赖的这些标记类型，
// .NET Framework 的 BCL 里没有，只在 .NET 5+ 才有。这里补上声明，编译器就能过，
// 语法本身仍然可用（它只是元数据标记，运行时不需要特殊支持）。
//
// Core 与 UI 两个项目都用 Compile Link 引用本文件，各自生成一份 internal 副本，
// 不会跨程序集冲突。
//
// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices
{
    /// <summary>record 的 init 访问器需要此标记，缺少时编译器报 CS0518。</summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>required 成员需要此标记（C# 11）。</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>编译器为 required 生成的特性标记。</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;

        public string FeatureName { get; }

        public bool IsOptional { get; init; }

        public const string RefStructs = nameof(RefStructs);

        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

// ReSharper disable once CheckNamespace
namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>标注“这个构造函数已满足全部 required 成员”，C# 11 需要。</summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}

// ---------------------------------------------------------------------------
//  Index / Range：net48 没有，但 C# 的 s[2..]、s[..dot] 语法会被编译成对它们的调用。
//  补上之后业务代码里的范围切片语法一行都不用改。
// ---------------------------------------------------------------------------
// ReSharper disable once CheckNamespace
namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0 && !fromEnd)
                throw new ArgumentOutOfRangeException(nameof(value), "Index 不能为负数");

            _value = fromEnd ? ~value : value;
        }

        public static Index Start => new Index(0);

        public static Index End => new Index(0, true);

        public bool IsFromEnd => _value < 0;

        public int Value => IsFromEnd ? ~_value : _value;

        public static implicit operator Index(int value) => new Index(value);

        public static Index FromStart(int value) => new Index(value);

        public static Index FromEnd(int value) => new Index(value, true);

        /// <summary>把“从末尾数”换算成实际下标；Roslyn 为重载切片生成的代码会调用它。</summary>
        public int GetOffset(int length) => IsFromEnd ? length + _value + 1 : _value;

        public bool Equals(Index other) => _value == other._value;

        public override bool Equals(object? obj) => obj is Index other && Equals(other);

        public override int GetHashCode() => _value;

        public override string ToString() => IsFromEnd ? "^" + Value.ToString() : Value.ToString();

        public static bool operator ==(Index left, Index right) => left.Equals(right);

        public static bool operator !=(Index left, Index right) => !left.Equals(right);
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public Index Start { get; }

        public Index End { get; }

        public static Range All => new Range(Index.Start, Index.End);

        public static Range StartAt(Index start) => new Range(start, Index.End);

        public static Range EndAt(Index end) => new Range(Index.Start, end);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var start = Start.GetOffset(length);
            var end = End.GetOffset(length);

            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length), "范围超出字符串长度");

            return (start, end - start);
        }

        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);

        public override bool Equals(object? obj) => obj is Range other && Equals(other);

        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();

        public override string ToString() => Start + ".." + End;

        public static bool operator ==(Range left, Range right) => left.Equals(right);

        public static bool operator !=(Range left, Range right) => !left.Equals(right);
    }

}


