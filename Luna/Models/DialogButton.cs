using System.Windows.Input;

namespace Luna.Models;

public class DialogButton
{
    public string Text { get; set; } = string.Empty;
    public ICommand? Command { get; set; }
    public object? CommandParameter { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsCancel { get; set; }
}