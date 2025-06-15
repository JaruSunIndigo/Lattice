using System.Collections;
using System.Collections.Generic;

namespace Lattice.Utils
{
    /// <summary>
    /// Like a normal dictionary, except stores a List of values for every key.
    /// </summary>
    public class MultiDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, List<TValue>>>
    {
        private readonly Dictionary<TKey, List<TValue>> dictionary;

        public MultiDictionary()
        {
            dictionary = new Dictionary<TKey, List<TValue>>();
        }

        // Add a value to the list associated with the key.
        public void Add(TKey key, TValue value)
        {
            if (!dictionary.ContainsKey(key))
            {
                dictionary[key] = new List<TValue>();
            }

            dictionary[key].Add(value);
        }

        // Remove a value from the list associated with the key.
        public bool Remove(TKey key, TValue value)
        {
            if (!dictionary.ContainsKey(key))
            {
                return false;
            }

            bool removed = dictionary[key].Remove(value);

            // If the list is empty, remove the key from the dictionary.
            if (dictionary[key].Count == 0)
            {
                dictionary.Remove(key);
            }

            return removed;
        }

        // Check if the dictionary contains the key.
        public bool ContainsKey(TKey key)
        {
            return dictionary.ContainsKey(key);
        }

        // Check if the dictionary contains the value for the given key.
        public bool ContainsValue(TKey key, TValue value)
        {
            if (!dictionary.ContainsKey(key))
            {
                return false;
            }

            return dictionary[key].Contains(value);
        }

        // Get the list of values associated with the key.
        public List<TValue> GetValues(TKey key)
        {
            if (!dictionary.ContainsKey(key))
            {
                return null;
            }

            return dictionary[key];
        }

        // Remove all values associated with the key.
        public void RemoveKey(TKey key)
        {
            dictionary.Remove(key);
        }

        // Get the number of key-value pairs in the dictionary.
        public int Count
        {
            get { return dictionary.Count; }
        }

        // Clear the dictionary.
        public void Clear()
        {
            dictionary.Clear();
        }

        // Implement the IEnumerable interface to allow iteration over the key-value pairs.
        public IEnumerator<KeyValuePair<TKey, List<TValue>>> GetEnumerator()
        {
            return dictionary.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
