using System.Collections.Immutable;

namespace ScanFlowOcr.Contracts;

public static class AlgorithmParameterValidator
{
    public static bool Validate(
        ImmutableArray<AlgorithmParameterDescriptor> descriptors,
        IReadOnlyDictionary<string, string> parameters,
        out string? error)
    {
        if (descriptors.IsDefaultOrEmpty)
        {
            error = null;
            return true;
        }

        var map = descriptors.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in parameters)
        {
            if (!map.TryGetValue(key, out var desc))
            {
                error = $"未知的算法参数：{key}";
                return false;
            }

            switch (desc.Type)
            {
                case AlgorithmParameterType.Boolean:
                    if (!bool.TryParse(value, out _))
                    {
                        error = $"参数【{desc.DisplayName}】值无效，必须为 true 或 false。";
                        return false;
                    }
                    break;

                case AlgorithmParameterType.Choice:
                    if (!desc.Options.IsDefaultOrEmpty && !desc.Options.Any(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase)))
                    {
                        error = $"参数【{desc.DisplayName}】选项值【{value}】无效。";
                        return false;
                    }
                    break;

                case AlgorithmParameterType.Integer:
                    if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int iv))
                    {
                        error = $"参数【{desc.DisplayName}】必须为整数。";
                        return false;
                    }
                    if (desc.MinIntValue.HasValue && iv < desc.MinIntValue.Value)
                    {
                        error = $"参数【{desc.DisplayName}】不能小于 {desc.MinIntValue.Value}。";
                        return false;
                    }
                    if (desc.MaxIntValue.HasValue && iv > desc.MaxIntValue.Value)
                    {
                        error = $"参数【{desc.DisplayName}】不能大于 {desc.MaxIntValue.Value}。";
                        return false;
                    }
                    break;
            }
        }

        error = null;
        return true;
    }
}
