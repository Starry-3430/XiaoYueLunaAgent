using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Luna.Services.Tools;

public sealed class VolumeBrightnessTool : ITool
{
    private static readonly JsonElement Schema = ToolSchema.Parse("""{"type":"object","properties":{"target":{"type":"string","enum":["volume","brightness"]},"value":{"type":"number","description":"目标百分比 0-100；volume 省略时返回状态"},"muted":{"type":"boolean","description":"volume 是否静音"}},"required":["target"]}""");
    private readonly ILogger<VolumeBrightnessTool> _logger;
    public VolumeBrightnessTool(ILogger<VolumeBrightnessTool> logger) => _logger = logger;
    public string Name => "volume_brightness";
    public string DisplayName => ToolLocalization.DisplayName(Name);
    public string Description => ToolLocalization.Description(Name);
    public JsonElement ParametersSchema => Schema;
    public ToolRiskLevel Risk => ToolRiskLevel.High;

    public Task<ToolResult> ExecuteAsync(JsonElement args, CancellationToken ct = default)
    {
        var target = GetString(args, "target").ToLowerInvariant();
        if (target is not ("volume" or "brightness")) return Task.FromResult(ToolResult.Error(ToolLocalization.Text("Tool_VolumeBrightness_Msg_InvalidTarget")));
        if (target == "volume") return Task.FromResult(HandleVolume(args));
        return Task.FromResult(HandleBrightness(args, ct));
    }

    private ToolResult HandleVolume(JsonElement args)
    {
        var endpoint = ActivateEndpoint();
        if (endpoint is null) return ToolResult.Error(ToolLocalization.Text("Tool_VolumeBrightness_Msg_NoAudio"));
        try
        {
            endpoint.GetMasterVolumeLevelScalar(out var current); endpoint.GetMute(out var muted);
            if (TryGetNumber(args, "value", out var value)) endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(value / 100d, 0, 1), Guid.Empty);
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("muted", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False) endpoint.SetMute(m.GetBoolean(), Guid.Empty);
            endpoint.GetMasterVolumeLevelScalar(out var updated); endpoint.GetMute(out var updatedMuted);
            Marshal.ReleaseComObject(endpoint);
            return ToolResult.Ok(ToolLocalization.Text("Tool_VolumeBrightness_Msg_Volume", updated * 100, updatedMuted ? "是" : "否"));
        }
        catch (Exception ex) { _logger.LogError(ex, "音量操作失败"); return ToolResult.Error(ToolLocalization.Text("Tool_VolumeBrightness_Msg_VolumeFailed", ex.Message)); }
    }

    private ToolResult HandleBrightness(JsonElement args, CancellationToken ct)
    {
        if (!TryGetNumber(args, "value", out var value)) return ToolResult.Error(ToolLocalization.Text("Tool_VolumeBrightness_Msg_BrightnessRequired"));
        if (ct.IsCancellationRequested) return ToolResult.Error(ToolLocalization.Text("Tool_VolumeBrightness_Msg_Cancelled"));
        var level = Math.Clamp((int)Math.Round(value), 0, 100);
        var script = "$monitors = Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods; foreach ($monitor in $monitors) { $monitor.WmiSetBrightness(1," + level + ") }";
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-NonInteractive"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(script);
        try
        {
            using var process = Process.Start(psi)!; process.WaitForExit(10_000); var error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0) return ToolResult.Error(string.IsNullOrWhiteSpace(error) ? ToolLocalization.Text("Tool_VolumeBrightness_Msg_BrightnessUnsupported") : ToolLocalization.Text("Tool_VolumeBrightness_Msg_BrightnessFailed", error.Trim()));
            return ToolResult.Ok(ToolLocalization.Text("Tool_VolumeBrightness_Msg_BrightnessSet", level));
        }
        catch (Exception ex) { _logger.LogError(ex, "亮度操作失败"); return ToolResult.Error($"亮度操作失败：{ex.Message}"); }
    }

    private static bool TryGetNumber(JsonElement args, string name, out double value) { value = 0; return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.TryGetDouble(out value); }
    private static string GetString(JsonElement args, string name) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? string.Empty : string.Empty;

    private static IAudioEndpointVolume? ActivateEndpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator(); enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device); var iid = typeof(IAudioEndpointVolume).GUID; device.Activate(ref iid, 23, IntPtr.Zero, out var result); Marshal.ReleaseComObject(device); Marshal.ReleaseComObject(enumerator); return (IAudioEndpointVolume)result;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDeviceEnumerator { int EnumAudioEndpoints(EDataFlow flow, uint state, out IntPtr devices); int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IMMDevice { int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result); }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAudioEndpointVolume { int RegisterControlChangeNotify(IntPtr notify); int UnregisterControlChangeNotify(IntPtr notify); int GetChannelCount(out uint count); int SetMasterVolumeLevel(float levelDb, Guid eventContext); int SetMasterVolumeLevelScalar(float level, Guid eventContext); int GetMasterVolumeLevel(out float levelDb); int GetMasterVolumeLevelScalar(out float level); int SetChannelVolumeLevel(uint channel, float levelDb, Guid eventContext); int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext); int GetChannelVolumeLevel(uint channel, out float levelDb); int GetChannelVolumeLevelScalar(uint channel, out float level); int SetMute(bool mute, Guid eventContext); int GetMute(out bool mute); }
}
