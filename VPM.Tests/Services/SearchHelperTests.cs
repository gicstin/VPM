using Xunit;
using VPM.Services;

namespace VPM.Tests.Services
{
    public class SearchHelperTests
    {
        [Fact]
        public void StartsWithSearch_EmptySearchTerm_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch("some text", "");

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_NullSearchTerm_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch("some text", null);

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_MatchingStartWithExactCase_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch("Hello World", "Hello");

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_MatchingStartWithDifferentCase_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch("Hello World", "hello");

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_MatchingStartWithDifferentCase2_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch("hello world", "HELLO");

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_NotMatchingStart_ReturnsFalse()
        {
            var result = SearchHelper.StartsWithSearch("Hello World", "World");

            Assert.False(result);
        }

        [Fact]
        public void StartsWithSearch_PartialMatch_ReturnsFalse()
        {
            var result = SearchHelper.StartsWithSearch("Hello World", "lo Wo");

            Assert.False(result);
        }

        [Fact]
        public void StartsWithSearch_NullText_ReturnsFalse()
        {
            var result = SearchHelper.StartsWithSearch(null, "search");

            Assert.False(result);
        }

        [Fact]
        public void StartsWithSearch_EmptyText_ReturnsFalse()
        {
            var result = SearchHelper.StartsWithSearch("", "search");

            Assert.False(result);
        }

        [Fact]
        public void StartsWithSearch_NullTextAndNullSearchTerm_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch(null, null);

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_EmptyTextAndEmptySearchTerm_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch("", "");

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_SearchTermLongerThanText_ReturnsFalse()
        {
            var result = SearchHelper.StartsWithSearch("Hi", "Hello");

            Assert.False(result);
        }

        [Fact]
        public void ContainsSearch_EmptySearchTerm_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("some text", "");

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_NullSearchTerm_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("some text", null);

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_ExactMatch_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("Hello World", "Hello World");

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_PartialMatchAtStart_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("Hello World", "Hello");

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_PartialMatchInMiddle_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("Hello World", "lo Wo");

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_PartialMatchAtEnd_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("Hello World", "World");

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_CaseInsensitiveMatch_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("Hello World", "hello world");

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_CaseInsensitivePartialMatch_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("Hello World", "WORLD");

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_NoMatch_ReturnsFalse()
        {
            var result = SearchHelper.ContainsSearch("Hello World", "xyz");

            Assert.False(result);
        }

        [Fact]
        public void ContainsSearch_NullText_ReturnsFalse()
        {
            var result = SearchHelper.ContainsSearch(null, "search");

            Assert.False(result);
        }

        [Fact]
        public void ContainsSearch_EmptyText_ReturnsFalse()
        {
            var result = SearchHelper.ContainsSearch("", "search");

            Assert.False(result);
        }

        [Fact]
        public void ContainsSearch_SearchTermLongerThanText_ReturnsFalse()
        {
            var result = SearchHelper.ContainsSearch("Hi", "Hello World");

            Assert.False(result);
        }

        [Fact]
        public void PrepareSearchText_NullInput_ReturnsEmptyString()
        {
            var result = SearchHelper.PrepareSearchText(null);

            Assert.Empty(result);
        }

        [Fact]
        public void PrepareSearchText_EmptyInput_ReturnsEmptyString()
        {
            var result = SearchHelper.PrepareSearchText("");

            Assert.Empty(result);
        }

        [Fact]
        public void PrepareSearchText_WhitespaceInput_ReturnsEmptyString()
        {
            var result = SearchHelper.PrepareSearchText("   ");

            Assert.Empty(result);
        }

        [Fact]
        public void PrepareSearchText_ValidInput_ReturnsTrimmed()
        {
            var result = SearchHelper.PrepareSearchText("  Hello World  ");

            Assert.Equal("Hello World", result);
        }

        [Fact]
        public void PrepareSearchText_ValidInputWithoutSpaces_ReturnsAsIs()
        {
            var result = SearchHelper.PrepareSearchText("HelloWorld");

            Assert.Equal("HelloWorld", result);
        }

        [Fact]
        public void MatchesPackageSearch_EmptySearchTerm_ReturnsTrue()
        {
            var result = SearchHelper.MatchesPackageSearch("MyPackage", "");

            Assert.True(result);
        }

        [Fact]
        public void MatchesPackageSearch_MatchingName_ReturnsTrue()
        {
            var result = SearchHelper.MatchesPackageSearch("MyPackage", "MyPack");

            Assert.True(result);
        }

        [Fact]
        public void MatchesPackageSearch_NonMatchingName_ReturnsFalse()
        {
            var result = SearchHelper.MatchesPackageSearch("MyPackage", "xyz");

            Assert.False(result);
        }

        [Fact]
        public void MatchesPackageSearch_CaseInsensitiveMatch_ReturnsTrue()
        {
            var result = SearchHelper.MatchesPackageSearch("MyPackage", "mypack");

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_SpecialCharacters_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch("$SpecialChars-123", "$Special");

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_SpecialCharacters_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("Some-$pecial_Chars", "$peCial");

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_UnicodeCharacters_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch("Café au lait", "café");

            Assert.True(result);
        }

        [Fact]
        public void ContainsSearch_UnicodeCharacters_ReturnsTrue()
        {
            var result = SearchHelper.ContainsSearch("This Café is nice", "café");

            Assert.True(result);
        }

        [Fact]
        public void StartsWithSearch_MixedCase_ReturnsTrue()
        {
            var result = SearchHelper.StartsWithSearch("HeLLo WoRLd", "HeLLo");

            Assert.True(result);
        }

        [Fact]
        public void PrepareSearchText_TabsAndNewlines_ReturnsTrimmed()
        {
            var result = SearchHelper.PrepareSearchText("\t\nHello\t\n");

            Assert.Equal("Hello", result);
        }
    }
}
