namespace Luna.Models;

/// <summary>日记浏览用的单日视图模型。</summary>
public class DiaryDayView
{
    public string LogicalDate { get; set; } = string.Empty;

    /// <summary>Pending / Done / Failed / NoData</summary>
    public string Status { get; set; } = "NoData";

    public string? Content { get; set; }
    public string? LastError { get; set; }
    public bool IsToday { get; set; }

    public bool HasContent => Status == "Done" && !string.IsNullOrWhiteSpace(Content);
    public bool IsPending => Status == "Pending";
    public bool IsNoData => Status == "NoData";
    public bool IsFailed => Status == "Failed";
}
