namespace Numenius.Core.Interfaces
{
    public interface ITextNormalizer
    {
        /// <summary>
        /// Приводит слово к нормализованной форме (стему или лемме).
        /// </summary>
        string Normalize(string word);

        /// <summary>
        /// Нормализует целую фразу (приводит к нижнему регистру, удаляет ё, мягкий знак и т.д.)
        /// и разбивает на слова, нормализуя каждое.
        /// </summary>
        string NormalizeText(string text);
    }
}