using System.Linq;

namespace FunscriptToolbox.SubtitlesVerbs.Translations
{
    public class TranslationExample
    {
        public string Original { get; }
        public string Translation { get; }

        public TranslationExample(string original, string translation)
        {
            this.Original = original;
            this.Translation = translation;
        }

        public static TranslationExample[] CreateTranslationExamples(
            Language originalLanguage,
            Language translationLanguage)
        {
            var examples = new[]
            {
                new[]
                {
                    new { Language = "English", Text = "This is not part of the video," },
                    new { Language = "Japanese", Text = "これは動画の一部ではありません。" },
                    new { Language = "Spanish", Text = "Esto no es parte del video," },
                    new { Language = "French", Text = "Ce n'est pas une partie de la vidéo," },
                    new { Language = "Korean", Text = "이것은 비디오의 일부가 아닙니다." },
                    new { Language = "German", Text = "Das ist kein Teil des Videos." },
                    new { Language = "Chinese", Text = "这不是视频的一部分。" }
                },
                new[]
                {
                    new { Language = "English", Text = "it's only an example..." },
                    new { Language = "Japanese", Text = "これはただの例です..." },
                    new { Language = "Spanish", Text = "es solo un ejemplo..." },
                    new { Language = "French", Text = "c'est juste un exemple..." },
                    new { Language = "Korean", Text = "그것은 단지 예제입니다..." },
                    new { Language = "German", Text = "Es ist nur ein Beispiel..." },
                    new { Language = "Chinese", Text = "这只是一个例子..." }
                },
                new[]
                {
                    new { Language = "English", Text = "to make sure that you understand," },
                    new { Language = "Japanese", Text = "あなたが理解していることを確認するために、" },
                    new { Language = "Spanish", Text = "para asegurarse de que entiende," },
                    new { Language = "French", Text = "pour s'assurer que vous comprenez," },
                    new { Language = "Korean", Text = "당신이 이해했는지 확인하기 위해서는," },
                    new { Language = "German", Text = "Um sicherzustellen, dass Sie verstehen," },
                    new { Language = "Chinese", Text = "为了确保您理解，" }
                },
                new[]
                {
                    new { Language = "English", Text = "the format that I want you to follow." },
                    new { Language = "Japanese", Text = "私があなたに従ってほしい形式です。" },
                    new { Language = "Spanish", Text = "el formato que quiero que sigas." },
                    new { Language = "French", Text = "le format que je veux que vous suiviez." },
                    new { Language = "Korean", Text = "나가 원하는 형식입니다." },
                    new { Language = "German", Text = "das Format, dem ich folgen möchte." },
                    new { Language = "Chinese", Text = "我希望您遵循的格式。" }
                },
                new[]
                {
                    new { Language = "English", Text = "The text I'll provide next is the first one from the video." },
                    new { Language = "Japanese", Text = "次に提供するテキストは、動画の最初のものです。" },
                    new { Language = "Spanish", Text = "El texto que proporcionaré a continuación es el primero del video." },
                    new { Language = "French", Text = "Le texte que je fournirai ensuite est le premier de la vidéo." },
                    new { Language = "Korean", Text = "다음으로 제공 할 텍스트는 비디오에서 첫 번째입니다." },
                    new { Language = "German", Text = "Der nächste Text, den ich bereitstellen werde, ist der erste im Video." },
                    new { Language = "Chinese", Text = "我下面要提供的文本是视频中的第一个。" }
                }
            };

            return examples.Select(example =>
            {
                var englishText = example.First(f => f.Language == "English").Text;

                var original = example.FirstOrDefault(f => f.Language == originalLanguage.LongName)?.Text
                    ?? TranslatorGoogleV1API.SimpleTranslate(englishText, "en", originalLanguage.ShortName);
                var translation = example.FirstOrDefault(f => f.Language == translationLanguage.LongName)?.Text
                    ?? TranslatorGoogleV1API.SimpleTranslate(englishText, "en", translationLanguage.ShortName);

                return new TranslationExample(original, translation);
            }).ToArray();
        }
    }
}