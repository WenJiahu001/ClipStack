using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace ClipStack.Core;

// 调用方负责同步；同时限制估算字节和条数，命中后保留最近使用的数据。
public sealed class BoundedCache<T>
{
    private sealed record Item(string Key, T Value, long Bytes);
    private readonly Dictionary<string, LinkedListNode<Item>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Item> order = new();
    private readonly long byteLimit;
    private readonly int countLimit;
    public long Bytes { get; private set; }
    public int Count => entries.Count;

    public BoundedCache(long byteLimit, int countLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countLimit);
        this.byteLimit = byteLimit; this.countLimit = countLimit;
    }
    public bool TryGet(string key, [MaybeNullWhen(false)] out T value)
    {
        if (!entries.TryGetValue(key, out var node)) { value = default; return false; }
        order.Remove(node); order.AddLast(node); value = node.Value.Value; return true;
    }
    public void Set(string key, T value, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Remove(key);
        if (bytes > byteLimit) return;
        while (order.First != null && (Bytes > byteLimit - bytes || Count >= countLimit)) Remove(order.First.Value.Key);
        var node = order.AddLast(new Item(key, value, bytes));
        entries.Add(key, node); Bytes += bytes;
    }
    public void Remove(string key)
    {
        if (!entries.Remove(key, out var node)) return;
        order.Remove(node); Bytes -= node.Value.Bytes;
    }
    public void Clear() { entries.Clear(); order.Clear(); Bytes = 0; }
}
