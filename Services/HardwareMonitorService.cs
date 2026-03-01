using LibreHardwareMonitor.Hardware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace mnmbox_tui.Services;

public class CpuInfo
{
    public string Name { get; set; } = "";
    public float? Temperature { get; set; }
    public float? PackagePower { get; set; }
    public float? CoreVoltage { get; set; }
    public float? CoreClock { get; set; }
    public float? Load { get; set; }
    public float? Tdp { get; set; }
}

public class GpuInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public float? Temperature { get; set; }
    public float? HotSpotTemperature { get; set; }
    public float? Power { get; set; }
    public float? CoreClock { get; set; }
    public float? MemoryClock { get; set; }
    public float? Load { get; set; }
    public float? MemoryLoad { get; set; }
    public float? MemoryUsed { get; set; }
    public float? MemoryTotal { get; set; }
    public float? FanSpeed { get; set; }
    public float? FanSpeedPercent { get; set; }
}

public class MemoryInfo
{
    public float? Used { get; set; }
    public float? Available { get; set; }
    public float? Total { get; set; }
    public float? LoadPercent => Total > 0 ? (Used / Total) * 100 : 0;
}

public class StorageInfo
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public float? Temperature { get; set; }
    public float? UsedSpace { get; set; }
    public float? AvailableSpace { get; set; }
    public float? TotalSpace { get; set; }
    public float? LoadPercent => TotalSpace > 0 ? (UsedSpace / TotalSpace) * 100 : 0;
}

public class NetworkInfo
{
    public string Name { get; set; } = "";
    public float? DownloadSpeed { get; set; }
    public float? UploadSpeed { get; set; }
}

public class HardwareMonitorService : IDisposable
{
    private Computer? _computer;
    private bool _isLhmInitialized = false;
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramCounter;
    private Dictionary<string, (float upload, float download)> _lastNetworkStats = new();
    private DateTime _lastNetworkCheck = DateTime.MinValue;

    public HardwareMonitorService()
    {
        TryInitializeLhm();
        TryInitializePerformanceCounters();
    }

    private void TryInitializeLhm()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsNetworkEnabled = true,
                IsControllerEnabled = true,
            };

            _computer.Open();
            _computer.Accept(new UpdateVisitor());
            _isLhmInitialized = true;
        }
        catch
        {
            _isLhmInitialized = false;
        }
    }

    private void TryInitializePerformanceCounters()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue(); // First call always returns 0
            
            _ramCounter = new PerformanceCounter("Memory", "% Committed Bytes In Use");
        }
        catch
        {
            // Performance counters might not be available
        }
    }

    public void Update()
    {
        if (_isLhmInitialized && _computer != null)
        {
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();
                foreach (var sub in hardware.SubHardware)
                {
                    sub.Update();
                }
            }
        }
    }

    public CpuInfo GetCpuInfo()
    {
        var info = new CpuInfo();

        // Try LHM first
        if (_isLhmInitialized && _computer != null)
        {
            var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            if (cpu != null)
            {
                info.Name = cpu.Name;

                foreach (var sensor in cpu.Sensors)
                {
                    if (!sensor.Value.HasValue) continue;

                    // Temperature - try various sensor names
                    if (sensor.SensorType == SensorType.Temperature)
                    {
                        if (info.Temperature == null && 
                            (sensor.Name.Contains("Tctl") || sensor.Name.Contains("Package") || 
                             sensor.Name.Contains("Core Average") || sensor.Name.Contains("CPU")))
                        {
                            info.Temperature = sensor.Value;
                        }
                    }
                    // Power
                    else if (sensor.SensorType == SensorType.Power && 
                             (sensor.Name.Contains("Package") || sensor.Name.Contains("CPU")))
                    {
                        info.PackagePower = sensor.Value;
                    }
                    // Clock
                    else if (sensor.SensorType == SensorType.Clock && 
                             (sensor.Name.Contains("Core") || sensor.Name.Contains("Bus")))
                    {
                        if (info.CoreClock == null || sensor.Value > info.CoreClock)
                            info.CoreClock = sensor.Value;
                    }
                    // Load - try to get total CPU load from LHM
                    else if (sensor.SensorType == SensorType.Load && 
                             (sensor.Name.Contains("Total") || sensor.Name.Contains("Package")))
                    {
                        info.Load = sensor.Value;
                    }
                }
            }
        }

        // Fallback to WMI for temperature if LHM didn't provide it
        if (info.Temperature == null)
        {
            info.Temperature = GetCpuTemperatureFromWmi();
        }

        // Fallback to PerformanceCounter for load
        if (info.Load == null && _cpuCounter != null)
        {
            try
            {
                info.Load = _cpuCounter.NextValue();
            }
            catch { }
        }

        // Get CPU name from WMI if LHM didn't provide it
        if (string.IsNullOrEmpty(info.Name))
        {
            info.Name = GetCpuNameFromWmi();
        }

        return info;
    }

    private float? GetCpuTemperatureFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", 
                "SELECT * FROM MSAcpi_ThermalZoneTemperature");
            
            foreach (ManagementObject obj in searcher.Get())
            {
                var temp = obj["CurrentTemperature"] as uint?;
                if (temp.HasValue)
                {
                    // WMI returns temperature in tenths of Kelvin
                    return (temp.Value / 10.0f) - 273.15f;
                }
            }
        }
        catch { }

        // Try alternative WMI class for AMD
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            
            foreach (ManagementObject obj in searcher.Get())
            {
                var temp = obj["Temperature"] as uint?;
                if (temp.HasValue && temp.Value > 0)
                {
                    return temp.Value - 273.15f; // Convert from Kelvin
                }
            }
        }
        catch { }

        return null;
    }

    private string GetCpuNameFromWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Processor");
            
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"] as string;
                if (!string.IsNullOrEmpty(name))
                    return name.Trim();
            }
        }
        catch { }

        return "Unknown CPU";
    }

    public GpuInfo GetGpuInfo()
    {
        var info = new GpuInfo();

        if (_isLhmInitialized && _computer != null)
        {
            var gpu = _computer.Hardware.FirstOrDefault(h => 
                h.HardwareType == HardwareType.GpuNvidia || 
                h.HardwareType == HardwareType.GpuAmd ||
                h.HardwareType == HardwareType.GpuIntel);
            
            if (gpu != null)
            {
                info.Name = gpu.Name;
                info.Type = gpu.HardwareType switch
                {
                    HardwareType.GpuNvidia => "NVIDIA",
                    HardwareType.GpuAmd => "AMD",
                    HardwareType.GpuIntel => "Intel",
                    _ => "Unknown"
                };

                foreach (var sensor in gpu.Sensors)
                {
                    if (!sensor.Value.HasValue) continue;

                    if (sensor.SensorType == SensorType.Temperature)
                    {
                        if (sensor.Name.Contains("GPU Core") || sensor.Name.Contains("D3D"))
                            info.Temperature = sensor.Value;
                        else if (sensor.Name.Contains("Hot Spot"))
                            info.HotSpotTemperature = sensor.Value;
                    }
                    else if (sensor.SensorType == SensorType.Power)
                    {
                        info.Power = sensor.Value;
                    }
                    else if (sensor.SensorType == SensorType.Clock)
                    {
                        if (sensor.Name.Contains("GPU Core") || sensor.Name.Contains("Graphics"))
                            info.CoreClock = sensor.Value;
                        else if (sensor.Name.Contains("Memory") || sensor.Name.Contains("VRAM"))
                            info.MemoryClock = sensor.Value;
                    }
                    else if (sensor.SensorType == SensorType.Load)
                    {
                        if (sensor.Name.Contains("GPU Core") || sensor.Name.Contains("D3D"))
                            info.Load = sensor.Value;
                        else if (sensor.Name.Contains("Memory"))
                            info.MemoryLoad = sensor.Value;
                    }
                    else if (sensor.SensorType == SensorType.SmallData)
                    {
                        if (sensor.Name.Contains("Used"))
                            info.MemoryUsed = sensor.Value;
                        else if (sensor.Name.Contains("Total"))
                            info.MemoryTotal = sensor.Value;
                    }
                    else if (sensor.SensorType == SensorType.Fan)
                    {
                        info.FanSpeed = sensor.Value;
                    }
                    else if (sensor.SensorType == SensorType.Control)
                    {
                        info.FanSpeedPercent = sensor.Value;
                    }
                }
            }
        }

        // Fallback to WMI for GPU info
        if (string.IsNullOrEmpty(info.Name))
        {
            GetGpuInfoFromWmi(info);
        }

        return info;
    }

    private void GetGpuInfoFromWmi(GpuInfo info)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM FROM Win32_VideoController");
            
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"] as string;
                if (!string.IsNullOrEmpty(name) && !name.Contains("Basic Display"))
                {
                    info.Name = name.Trim();
                    var ram = obj["AdapterRAM"] as uint?;
                    if (ram.HasValue)
                        info.MemoryTotal = ram.Value / (1024 * 1024); // Convert to MB
                    break;
                }
            }
        }
        catch { }
    }

    public MemoryInfo GetMemoryInfo()
    {
        var info = new MemoryInfo();

        // Try LHM first
        if (_isLhmInitialized && _computer != null)
        {
            var memory = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
            if (memory != null)
            {
                foreach (var sensor in memory.Sensors)
                {
                    if (!sensor.Value.HasValue) continue;

                    if (sensor.SensorType == SensorType.Data)
                    {
                        if (sensor.Name.Contains("Used"))
                            info.Used = sensor.Value;
                        else if (sensor.Name.Contains("Available"))
                            info.Available = sensor.Value;
                    }
                }
            }
        }

        // Fallback to Windows API
        if (info.Used == null || info.Available == null)
        {
            try
            {
                var total = (float)(GC.GetTotalMemory(false) / (1024.0 * 1024.0 * 1024.0));
                // This is only managed memory, let's get physical memory
                var pc = new Microsoft.Win32.SafeHandles.SafeFileHandle(IntPtr.Zero, true);
            }
            catch { }

            // Use WMI for accurate memory info
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                
                foreach (ManagementObject obj in searcher.Get())
                {
                    var total = obj["TotalVisibleMemorySize"] as ulong?;
                    var free = obj["FreePhysicalMemory"] as ulong?;
                    
                    if (total.HasValue && free.HasValue)
                    {
                        // Values are in KB, convert to GB
                        // Calculate Total as sum
                        var totalGB = total.Value / (1024.0f * 1024.0f);
                        info.Available = free.Value / (1024.0f * 1024.0f);
                        info.Used = info.Total - info.Available;
                    }
                }
            }
            catch { }
        }

        return info;
    }

    public List<StorageInfo> GetStorageInfo()
    {
        var storages = new List<StorageInfo>();
        var lhmStorageNames = new HashSet<string>();

        // Try LHM first for temperatures
        if (_isLhmInitialized && _computer != null)
        {
            var storageHardware = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage);

            foreach (var storage in storageHardware)
            {
                var info = new StorageInfo 
                { 
                    Name = storage.Name,
                    Model = storage.Name
                };
                lhmStorageNames.Add(storage.Name);

                foreach (var sensor in storage.Sensors)
                {
                    if (!sensor.Value.HasValue) continue;

                    if (sensor.SensorType == SensorType.Temperature)
                    {
                        info.Temperature = sensor.Value;
                    }
                }

                storages.Add(info);
            }
        }

        // Use DriveInfo for accurate space information
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;

                // Find matching LHM storage or create new
                var driveLabel = drive.Name.TrimEnd('\\');
                var info = storages.FirstOrDefault(s => 
                    driveLabel.Contains(s.Name) || 
                    s.Name.Contains(driveLabel));

                if (info == null)
                {
                    info = new StorageInfo
                    {
                        Name = drive.Name.TrimEnd('\\'),
                        Model = drive.Name.TrimEnd('\\')
                    };
                    storages.Add(info);
                }

                // Convert bytes to GB
                info.TotalSpace = drive.TotalSize / (1024.0f * 1024.0f * 1024.0f);
                info.AvailableSpace = drive.AvailableFreeSpace / (1024.0f * 1024.0f * 1024.0f);
                info.UsedSpace = info.TotalSpace - info.AvailableSpace;
            }
        }
        catch { }

        return storages;
    }

    public List<NetworkInfo> GetNetworkInfo()
    {
        var networks = new List<NetworkInfo>();

        // Use LHM for speeds
        if (_isLhmInitialized && _computer != null)
        {
            var networkHardware = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Network);

            foreach (var net in networkHardware)
            {
                if (IsVirtualAdapter(net.Name))
                    continue;

                var info = new NetworkInfo { Name = net.Name };

                foreach (var sensor in net.Sensors)
                {
                    if (!sensor.Value.HasValue) continue;

                    if (sensor.SensorType == SensorType.Data)
                    {
                        if (sensor.Name.Contains("Download"))
                            info.DownloadSpeed = sensor.Value;
                        else if (sensor.Name.Contains("Upload"))
                            info.UploadSpeed = sensor.Value;
                    }
                }

                networks.Add(info);
            }
        }

        // Fallback to WMI
        if (networks.Count == 0)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, BytesReceivedPersec, BytesSentPersec FROM Win32_PerfFormattedData_Tcpip_NetworkInterface");
                
                foreach (ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"] as string;
                    if (string.IsNullOrEmpty(name) || IsVirtualAdapter(name))
                        continue;

                    var info = new NetworkInfo { Name = name };
                    
                    var recv = obj["BytesReceivedPersec"] as ulong?;
                    var sent = obj["BytesSentPersec"] as ulong?;
                    
                    // Convert to MB/s
                    if (recv.HasValue)
                        info.DownloadSpeed = recv.Value / (1024.0f * 1024.0f);
                    if (sent.HasValue)
                        info.UploadSpeed = sent.Value / (1024.0f * 1024.0f);

                    networks.Add(info);
                }
            }
            catch { }
        }

        return networks;
    }

    private bool IsVirtualAdapter(string name)
    {
        var virtualKeywords = new[]
        {
            "virtual", "virtualbox", "vmware", "hyper-v", "hyperv",
            "wfp", "filter", "lightweight", "ndis", "packet scheduler",
            "loopback", "pseudo", "tunnel", "teredo", "6to4",
            "isatap", "ppp", "vpn", "miniport", "tap-", "tun-",
            "bridge", "multiplexer", "debug", "netbalancer", "qos"
        };

        var lowerName = name.ToLowerInvariant();
        return virtualKeywords.Any(keyword => lowerName.Contains(keyword));
    }

    public void Dispose()
    {
        _computer?.Close();
        _cpuCounter?.Dispose();
        _ramCounter?.Dispose();
    }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer)
    {
        computer.Traverse(this);
    }

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this);
    }

    public void VisitSensor(ISensor sensor) { }

    public void VisitParameter(IParameter parameter) { }
}
