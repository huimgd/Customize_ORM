using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Customize_ORM.Extensions
{
    public class ConcurrentHashSet<T> : ISet<T> where T : notnull
    {
        private readonly ConcurrentDictionary<T, byte> _dict = new();

        public IEnumerator<T> GetEnumerator()
        {
            return _dict.Keys.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        void ICollection<T>.Add(T item)
        {
            _dict.TryAdd(item, 0);
        }

        public void ExceptWith(IEnumerable<T> other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            foreach (var item in other)
            {
                _dict.TryRemove(item, out _);
            }
        }

        public void IntersectWith(IEnumerable<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var otherSet = other as ISet<T> ?? new HashSet<T>(other);
            var keys = _dict.Keys.ToList();
            var keysToRemove = keys.Where(key => !otherSet.Contains(key)).ToList();

            foreach (var key in keysToRemove)
            {
                _dict.TryRemove(key, out _);
            }
        }

        public bool IsProperSubsetOf(IEnumerable<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var otherSet = other as ISet<T> ?? new HashSet<T>(other);
            if (_dict.Count >= otherSet.Count) return false;
            var keys = _dict.Keys.ToList();

            if (keys.Any(key => !otherSet.Contains(key)))
            {
                return false;
            }

            return otherSet.Count > _dict.Count;
        }

        public bool IsProperSupersetOf(IEnumerable<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var otherSet = other as ISet<T> ?? new HashSet<T>(other);
            if (_dict.Count <= otherSet.Count) return false;

            if (otherSet.Any(item => !_dict.ContainsKey(item)))
            {
                return false;
            }

            return _dict.Count > otherSet.Count;
        }

        public bool IsSubsetOf(IEnumerable<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var otherSet = other as ISet<T> ?? new HashSet<T>(other);
            if (_dict.Count > otherSet.Count) return false;
            var keys = _dict.Keys.ToList();

            return keys.All(key => otherSet.Contains(key));
        }

        public bool IsSupersetOf(IEnumerable<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var otherSet = other as ISet<T> ?? new HashSet<T>(other);
            return _dict.Count >= otherSet.Count && otherSet.All(item => _dict.ContainsKey(item));
        }

        public bool Overlaps(IEnumerable<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);

            return other.Any(item => _dict.ContainsKey(item));
        }

        public bool SetEquals(IEnumerable<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var otherSet = other as ISet<T> ?? new HashSet<T>(other);
            return _dict.Count == otherSet.Count && otherSet.All(item => _dict.ContainsKey(item));
        }

        public void SymmetricExceptWith(IEnumerable<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var otherSet = other as ISet<T> ?? new HashSet<T>(other);

            // 找出需要添加的元素（在其他集合中但不在当前集合中）
            var toAdd = otherSet.Where(item => !_dict.ContainsKey(item)).ToList();

            // 找出需要移除的元素（在当前集合中但不在其他集合中）
            var toRemove = _dict.Keys.Where(key => !otherSet.Contains(key)).ToList();

            // 执行移除
            foreach (var key in toRemove)
            {
                _dict.TryRemove(key, out _);
            }

            // 执行添加
            foreach (var item in toAdd)
            {
                _dict.TryAdd(item, 0);
            }
        }

        public void UnionWith(IEnumerable<T> other)
        {
            ArgumentNullException.ThrowIfNull(other);

            foreach (var item in other)
            {
                _dict.TryAdd(item, 0);
            }
        }

        bool ISet<T>.Add(T item)
        {
            return _dict.TryAdd(item, 0);
        }

        public void Clear()
        {
            _dict.Clear();
        }

        public bool Contains(T item)
        {
            return _dict.ContainsKey(item);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
            if (array.Length - arrayIndex < _dict.Count)
            {
                throw new ArgumentException("目标数组长度不足");
            }

            var index = arrayIndex;
            foreach (var key in _dict.Keys)
            {
                array[index++] = key;
            }
        }

        public bool Remove(T item)
        {
            return _dict.TryRemove(item, out _);
        }

        public int Count => _dict.Count;

        public bool IsReadOnly => false;

        // 添加一些便捷方法
        public bool Add(T item) => ((ISet<T>)this).Add(item);

        public void AddRange(IEnumerable<T> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            foreach (var item in items)
            {
                _dict.TryAdd(item, 0);
            }
        }

        public T[] ToArray() => _dict.Keys.ToArray();

        public List<T> ToList() => _dict.Keys.ToList();
    }
}
