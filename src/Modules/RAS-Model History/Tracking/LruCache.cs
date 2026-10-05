using System.Collections.Generic;

namespace RASv2.ModelHistory.Tracking
{
    /// <summary>Простой LRU-кэш (используется только из главного потока Revit).</summary>
    public sealed class LruCache<TKey, TValue>
    {
        private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map =
            new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>();
        private readonly LinkedList<KeyValuePair<TKey, TValue>> _list = new LinkedList<KeyValuePair<TKey, TValue>>();

        public LruCache(int capacity)
        {
            Capacity = capacity;
        }

        public int Capacity { get; set; }
        public int Count => _map.Count;

        public bool Contains(TKey key) => _map.ContainsKey(key);

        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                node.Value = new KeyValuePair<TKey, TValue>(key, value);
                _list.AddFirst(node);
                return;
            }
            node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
            _list.AddFirst(node);
            _map[key] = node;
            while (_map.Count > Capacity && _list.Last != null)
            {
                _map.Remove(_list.Last.Value.Key);
                _list.RemoveLast();
            }
        }

        public void Remove(TKey key)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _map.Remove(key);
            }
        }

        public void Clear()
        {
            _map.Clear();
            _list.Clear();
        }
    }
}
