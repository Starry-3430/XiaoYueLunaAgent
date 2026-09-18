using CommunityToolkit.Mvvm.ComponentModel;

namespace Luna.Models;

public partial class ChatMessage : ObservableObject
{
    [ObservableProperty]
    private string _role = "user";       // user / assistant / system

    [ObservableProperty]
    private string _content = string.Empty;
}