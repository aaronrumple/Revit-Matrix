using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace RevitJournalViewer;

/// <summary>Colors each visible line of the AvalonEdit output using <see cref="JournalClassifier"/>.</summary>
public sealed class JournalColorizer : DocumentColorizingTransformer
{
    /// <summary>Current palette. Call TextView.Redraw() after changing it.</summary>
    public Theme Theme { get; set; } = Theme.Dark;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0) return;
        string text = CurrentContext.Document.GetText(line);

        foreach (var span in JournalClassifier.Classify(text))
        {
            if (span.Length <= 0) continue;
            var style = Theme.StyleFor(span.Kind);
            int start = line.Offset + span.Start;
            ChangeLinePart(start, start + span.Length, el =>
            {
                el.TextRunProperties.SetForegroundBrush(style.Brush);
                if (style.Bold || style.Italic)
                {
                    var tf = el.TextRunProperties.Typeface;
                    el.TextRunProperties.SetTypeface(new Typeface(
                        tf.FontFamily,
                        style.Italic ? FontStyles.Italic : tf.Style,
                        style.Bold ? FontWeights.Bold : tf.Weight,
                        tf.Stretch));
                }
            });
        }
    }
}
