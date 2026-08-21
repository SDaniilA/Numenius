using System;
using Numenius.Core.Models;

namespace Numenius.Core.Interfaces
{
    public interface INlpParser
    {
        ParsedMessage Parse(string rawText, string sender, string sourceType, DateTime receivedAt);
    }
}