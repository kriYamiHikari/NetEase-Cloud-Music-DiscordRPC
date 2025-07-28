using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Kxnrl.Vanessa.Utils;

internal static class ProcessUtils
{
    private static readonly Dictionary<(int, string), nint> ModuleAddressCache = new();
    private static int _lastPid = -1;
    
    /// <summary>
    /// 获取指定进程中模块的基地址，利用缓存避免重复的慢速查询。
    /// </summary>
    public static nint GetModuleBaseAddress(int pid, string moduleName)
    {
        if (pid != _lastPid)
        {
            ModuleAddressCache.Clear();
            _lastPid = pid;
        }

        var cacheKey = (pid, moduleName);
        if (ModuleAddressCache.TryGetValue(cacheKey, out var cachedAddress))
        {
            return cachedAddress;
        }
        
        try
        {
            using var process = Process.GetProcessById(pid);
            foreach (ProcessModule module in process.Modules)
            {
                if (!module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)) continue;
                var baseAddress = module.BaseAddress;
                ModuleAddressCache[cacheKey] = baseAddress;
                return baseAddress;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] Failed to get modules for PID {pid}: {ex.Message}");
        }
        
        ModuleAddressCache[cacheKey] = IntPtr.Zero;
        return IntPtr.Zero;
    }
}