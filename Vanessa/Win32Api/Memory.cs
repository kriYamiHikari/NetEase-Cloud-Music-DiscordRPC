using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kxnrl.Vanessa.Win32Api;

internal static class Memory
{
    private static readonly Dictionary<(int, nint), (nint pStart, byte[] memory)> ModuleCache = new();
    private static int _lastProcessId = -1;

    public static bool FindPattern(string pattern, int processId, nint moduleBaseAddress, out nint pointer)
    {
        if (processId != _lastProcessId)
        {
            ModuleCache.Clear();
            _lastProcessId = processId;
        }

        (nint pStart, byte[] memoryBlock) cacheEntry;

        if (!ModuleCache.TryGetValue((processId, moduleBaseAddress), out cacheEntry))
        {
            var memory = new ProcessMemory(processId);

            try
            {
                var ntOffset = memory.ReadInt32(moduleBaseAddress, 0x3C);
                var ntHeader = moduleBaseAddress + ntOffset;

                var fileHeader = ntHeader + 4;
                var optHeader = fileHeader + 20;

                var sectionSize = memory.ReadInt16(fileHeader, 16);
                var sections = memory.ReadInt16(ntHeader, 6);
                var sectionHeader = optHeader + sectionSize;

                var cursor = sectionHeader;

                for (var i = 0; i < sections; i++)
                {
                    if (memory.ReadInt64(cursor) == 0x747865742E)
                    {
                        var pOffset = memory.ReadInt32(cursor, 12);
                        var pSize = memory.ReadInt32(cursor, 8);

                        cacheEntry.pStart = moduleBaseAddress + pOffset;
                        cacheEntry.memoryBlock = memory.ReadBytes(cacheEntry.pStart, pSize);

                        ModuleCache[(processId, moduleBaseAddress)] = cacheEntry;
                        break;
                    }

                    cursor += 40; // Size of IMAGE_SECTION_HEADER
                }
            }
            catch (Exception)
            {
                pointer = nint.Zero;
                return false;
            }
        }

        if (cacheEntry.memoryBlock is null)
        {
            pointer = nint.Zero;
            return false;
        }

        pointer = FindPattern(pattern, cacheEntry.pStart, cacheEntry.memoryBlock);

        return pointer != nint.Zero;
    }

    private static nint FindPattern(string pattern, nint pStart, byte[] memoryBlock)
    {
        if (string.IsNullOrEmpty(pattern) || pStart == nint.Zero || memoryBlock.Length == 0)
        {
            return nint.Zero;
        }

        var patternBytes = ParseSignature(pattern);
        var firstByte = patternBytes[0];
        var searchRange = memoryBlock.Length - patternBytes.Length;

        for (var i = 0; i < searchRange; i++)
        {
            if (firstByte != 0xFFFF)
            {
                i = Array.IndexOf(memoryBlock, (byte)firstByte, i);
                if (i == -1)
                {
                    break;
                }
            }
            
            var found = true;
            for (var j = 1; j < patternBytes.Length; j++)
            {
                if (patternBytes[j] == 0xFFFF || patternBytes[j] == memoryBlock[i + j]) continue;
                found = false;
                break;
            }

            if (found)
            {
                return nint.Add(pStart, i);
            }
        }

        return nint.Zero;
    }

    private static ushort[] ParseSignature(string signature)
    {
        var bytesStr = signature.Split(' ');
        var bytes = new ushort[bytesStr.Length];

        for (var i = 0; i < bytes.Length; i++)
        {
            var str = bytesStr[i];
            if (str.Contains('?'))
            {
                bytes[i] = 0xFFFF;
            }
            else
            {
                bytes[i] = Convert.ToByte(str, 16);
            }
        }

        return bytes;
    }
}

internal sealed class ProcessMemory
{
    private readonly nint _process;

    public ProcessMemory(nint process)
        => _process = process;

    public ProcessMemory(int processId)
        => _process = OpenProcess(0x0010, IntPtr.Zero, processId);

    public byte[] ReadBytes(IntPtr offset, int length)
    {
        var bytes = new byte[length];
        ReadProcessMemory(_process, offset, bytes, length, IntPtr.Zero);

        return bytes;
    }

    public float ReadFloat(IntPtr address, int offset = 0)
        => BitConverter.ToSingle(ReadBytes(IntPtr.Add(address, offset), 4), 0);

    public double ReadDouble(IntPtr address, int offset = 0)
        => BitConverter.ToDouble(ReadBytes(IntPtr.Add(address, offset), 8), 0);

    public long ReadInt64(IntPtr address, int offset = 0)
        => BitConverter.ToInt64(ReadBytes(IntPtr.Add(address, offset), 8), 0);

    public ulong ReadUInt64(IntPtr address, int offset = 0)
        => BitConverter.ToUInt64(ReadBytes(IntPtr.Add(address, offset), 8), 0);

    public short ReadInt16(IntPtr address, int offset = 0)
        => BitConverter.ToInt16(ReadBytes(IntPtr.Add(address, offset), 2), 0);

    public int ReadInt32(IntPtr address, int offset = 0)
        => BitConverter.ToInt32(ReadBytes(IntPtr.Add(address, offset), 4), 0);

    public uint ReadUInt32(IntPtr address, int offset = 0)
        => BitConverter.ToUInt32(ReadBytes(IntPtr.Add(address, offset), 4), 0);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr pHandle, IntPtr address, byte[] buffer, int size,
        IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int dwDesiredAccess, IntPtr bInheritHandle, int dwProcessId);
}