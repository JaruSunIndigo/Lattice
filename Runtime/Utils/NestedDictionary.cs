using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Lattice.Utils
{
    /// <summary>An ergonomic interface for Dictionary[K1, Dictionary[K2, V]].</summary>
    public class NestedDictionary<K1, K2, V> : IEnumerable<(K1, K2, V)>
    {
        private readonly Dictionary<K1, Dictionary<K2, V>> storage = new();

        /// <summary>Adds a value with the specified keys to the dictionary.</summary>
        public void Add(K1 key1, K2 key2, V value)
        {
            if (!storage.ContainsKey(key1))
            {
                storage[key1] = new Dictionary<K2, V>();
            }

            storage[key1].Add(key2, value);
        }

        /// <summary>Gets or sets the value associated with the specified keys.</summary>
        /// <param name="keys">The tuple containing keys (K1, K2) of the element to get or set.</param>
        public V this[(K1, K2) keys]
        {
            get
            {
                Dictionary<K2, V> subDictionary = storage[keys.Item1];
                return subDictionary[keys.Item2];
            }
            set
            {
                if (!storage.ContainsKey(keys.Item1))
                {
                    storage[keys.Item1] = new Dictionary<K2, V>();
                }

                storage[keys.Item1][keys.Item2] = value;
            }
        }

        /// <summary>Gets or sets the sub-dictionary associated with the specified key.</summary>
        /// <returns>The sub-dictionary associated with the specified key.</returns>
        public Dictionary<K2, V> this[K1 key1]
        {
            get => storage[key1];
            set => storage[key1] = value;
        }

        /// <summary>Attempts to get the value associated with the specified keys.</summary>
        /// <returns>true if the element is found; otherwise, false.</returns>
        public bool TryGetValue(K1 key1, K2 key2, out V value)
        {
            if (storage.TryGetValue(key1, out Dictionary<K2, V> subDictionary))
            {
                return subDictionary.TryGetValue(key2, out value);
            }

            value = default;
            return false;
        }
        
        /// <summary>Attempts to get the value associated with the specified key.</summary>
        /// <returns>true if the element is found; otherwise, false.</returns>
        public bool TryGetValue(K1 key1, out Dictionary<K2, V> value)
        {
            return storage.TryGetValue(key1, out value);
        }

        /// <summary>Removes the value with the specified keys from the dictionary.</summary>
        /// <returns>true if the element exists; otherwise, false.</returns>
        public bool Remove(K1 key1, K2 key2)
        {
            if (storage.TryGetValue(key1, out Dictionary<K2, V> subDictionary))
            {
                bool result = subDictionary.Remove(key2);

                // Optionally remove the sub-dictionary if it becomes empty
                if (subDictionary.Count == 0)
                {
                    storage.Remove(key1);
                }

                return result;
            }

            return false;
        }

        /// <summary>Removes the sub-dictionary with the specified key.</summary>
        /// <param name="key1">The key of the sub-dictionary to remove.</param>
        /// <returns>true if the sub-dictionary exists; otherwise, false.</returns>
        public bool Remove(K1 key1)
        {
            return storage.Remove(key1);
        }

        /// <summary>Removes all keys and values from the dictionary.</summary>
        public void Clear()
        {
            storage.Clear();
        }

        /// <summary>An enumerable collection of dictionary key pairs.</summary>
        public IEnumerable<(K1, K2)> Keys
        {
            get
            {
                return storage.SelectMany(
                    top => top.Value.Keys,
                    (top, sub) => (top.Key, sub)
                );
            }
        }
        /// <summary>An enumerable collection of all values in the nested dictionary.</summary>
        public IEnumerable<V> Values
        {
            get { return storage.SelectMany(sub => sub.Value.Values); }
        }

        public IEnumerator<(K1, K2, V)> GetEnumerator()
        {
            foreach (var outerPair in storage)
            {
                K1 key1 = outerPair.Key;
                foreach (var innerPair in outerPair.Value)
                {
                    K2 key2 = innerPair.Key;
                    V value = innerPair.Value;
                    yield return (key1, key2, value);
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
