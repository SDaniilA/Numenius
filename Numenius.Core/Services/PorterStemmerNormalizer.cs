using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Numenius.Core.Interfaces;

namespace Numenius.Core.Services
{
    public class PorterStemmerNormalizer : ITextNormalizer
    {
        private static readonly Dictionary<string, string> PerfectiveGerund = new()
        {
            { "ившись", "" }, { "ывшись", "" }, { "вшись", "" }, { "ивши", "" }, { "ывши", "" }, { "вши", "" },
            { "ив", "" }, { "ыв", "" }, { "в", "" }
        };

        private static readonly Dictionary<string, string> Reflexive = new()
        {
            { "ся", "" }, { "сь", "" }
        };

        private static readonly Dictionary<string, string> Adjective = new()
        {
            { "ее", "" }, { "ие", "" }, { "ые", "" }, { "ое", "" }, { "ей", "" }, { "ий", "" }, { "ый", "" },
            { "ой", "" }, { "ем", "" }, { "им", "" }, { "ым", "" }, { "ом", "" }, { "его", "" }, { "ого", "" },
            { "ему", "" }, { "ому", "" }, { "их", "" }, { "ых", "" }, { "ую", "" }, { "юю", "" }, { "ая", "" },
            { "яя", "" }, { "ою", "" }, { "ею", "" }
        };

        private static readonly Dictionary<string, string> Participle = new()
        {
            { "ем", "" }, { "нн", "" }, { "вш", "" }, { "ющ", "" }, { "щ", "" }, { "евш", "" }, { "овавш", "" },
            { "ивш", "" }, { "ывш", "" }, { "овав", "" }, { "евав", "" }, { "ова", "" }, { "ева", "" }, { "ив", "" }
        };

        private static readonly Dictionary<string, string> Verb = new()
        {
            { "ела", "" }, { "ыла", "" }, { "ена", "" }, { "ейте", "" }, { "уйте", "" }, { "ите", "" }, { "или", "" },
            { "ыли", "" }, { "ей", "" }, { "уй", "" }, { "ил", "" }, { "ыл", "" }, { "им", "" }, { "ым", "" }, { "ен", "" },
            { "ить", "" }, { "ыть", "" }, { "ешь", "" }, { "ете", "" }, { "йте", "" }
        };

        private static readonly Dictionary<string, string> Noun = new()
        {
            { "иями", "" }, { "ями", "" }, { "ами", "" }, { "ией", "" }, { "иям", "" }, { "ием", "" }, { "иях", "" },
            { "ев", "" }, { "ов", "" }, { "ие", "" }, { "ье", "" }, { "еи", "" }, { "ии", "" }, { "ей", "" }, { "ой", "" },
            { "ий", "" }, { "й", "" }, { "ия", "" }, { "ья", "" }, { "я", "" }, { "а", "" }, { "е", "" }, { "ы", "" },
            { "ь", "" }, { "и", "" }, { "у", "" }, { "ю", "" }, { "о", "" }
        };

        private static readonly Dictionary<string, string> Superlative = new()
        {
            { "ейше", "" }, { "ейш", "" }
        };

        private static readonly Dictionary<string, string> Derivational = new()
        {
            { "ост", "" }, { "ость", "" }
        };

        public string Normalize(string word)
        {
            if (string.IsNullOrEmpty(word)) return string.Empty;
            word = word.ToLowerInvariant();
            word = word.Replace('ё', 'е');
            word = word.Replace("ь", "");

            foreach (var rule in PerfectiveGerund.Concat(Reflexive).Concat(Adjective).Concat(Participle)
                         .Concat(Verb).Concat(Noun).Concat(Superlative).Concat(Derivational))
            {
                if (word.Length >= rule.Key.Length + 2 && word.EndsWith(rule.Key))
                {
                    word = word.Substring(0, word.Length - rule.Key.Length);
                    break;
                }
            }
            return word;
        }

        public string NormalizeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.ToLowerInvariant();
            text = text.Replace('ё', 'е');
            text = text.Replace("ь", "");
            var words = Regex.Split(text, @"[^а-яё0-9\-]+");
            return string.Join(" ", words.Select(Normalize));
        }
    }
}