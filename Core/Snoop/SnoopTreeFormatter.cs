using System.Text;
using TurboSuite.Snoop.Models;

namespace TurboSuite.Snoop;

/// <summary>
/// Flattens a <see cref="SnoopNode"/> tree to indented monospace-friendly text. Spike-only: the shipping
/// module will bind the tree to a WPF TreeView, but for answering the symbolic-line question a text dump
/// in a TaskDialog is the fastest read. Pure (no Revit refs) so it lives in Core alongside the model.
/// </summary>
public static class SnoopTreeFormatter
{
    public static string ToIndentedText(SnoopNode root)
    {
        var sb = new StringBuilder();
        Write(sb, root, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, SnoopNode node, int depth)
    {
        sb.Append(' ', depth * 3);
        sb.Append(Glyph(node.Kind));
        sb.Append(' ');
        sb.AppendLine(node.Label);

        foreach (SnoopNode child in node.Children)
            Write(sb, child, depth + 1);
    }

    private static string Glyph(SnoopNodeKind kind) => kind switch
    {
        SnoopNodeKind.Family => "▸",
        SnoopNodeKind.Category => "▸",
        SnoopNodeKind.Subcategory => "•",
        _ => "·",
    };
}
