using Butterfly.Serialization.Tests;

namespace Butterfly.Serialization.Csv.Tests
{
    public sealed record Person(string Name, int Age, decimal Salary, Status Status, Address? Home, DateTimeOffset Joined, bool Active, double? Score);

    public class CsvFormatTests
    {
        private static readonly CsvFormat Csv = CsvFormat.Instance;

        private static readonly List<Person> People =
        [
            new("Ada", 36, 1500.5m, Status.Active, new Address("Main 1", "London"), new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), true, 9.5),
            new("Bob, \"the builder\"", 41, 0m, Status.Archived, null, new DateTimeOffset(2025, 5, 6, 7, 8, 9, TimeSpan.FromHours(-3)), false, null),
            new("Multi\nline", 1, -2m, Status.Draft, new Address(" spaced ", "X", "Z"), default, true, double.NaN)
        ];

        [Fact]
        public void WritesAHeaderAndOneRowPerObject()
        {
            var csv = ButterflySerializer.SerializeToString(People, Csv);
            var lines = csv.Split("\r\n");

            Assert.Equal("Name,Age,Salary,Status,Home.Street,Home.City,Joined,Active,Score,Home.Zip", lines[0]);
            Assert.Equal("Ada,36,1500.5,Active,Main 1,London,2026-01-02T03:04:05.0000000+00:00,true,9.5,", lines[1]);
            Assert.StartsWith("\"Bob, \"\"the builder\"\"\",41,0,archived,,,", lines[2]);
        }

        [Fact]
        public void RoundTrips()
        {
            var csv = ButterflySerializer.SerializeToString(People, Csv);

            var read = ButterflySerializer.Deserialize<List<Person>>(csv, Csv);

            Assert.Equal(People.Count, read.Count);
            Assert.Equal(People[0], read[0]);
            Assert.Equal(People[1], read[1]);
            Assert.Equal("Multi\nline", read[2].Name);
            Assert.Equal(new Address(" spaced ", "X", "Z"), read[2].Home);
            Assert.True(double.IsNaN(read[2].Score!.Value));
        }

        [Fact]
        public void ReadsASingleObject()
        {
            var person = ButterflySerializer.Deserialize<Person>("Name;Age;Home.City\r\nAda;36;London\r\n", CsvFormat.Semicolon);

            Assert.Equal("Ada", person.Name);
            Assert.Equal(36, person.Age);
            Assert.Equal("London", person.Home!.City);
        }

        [Fact]
        public void ProfilesChooseColumns()
        {
            var csv = ButterflySerializer.SerializeToString(new List<Customer> { new() { Id = 1, Name = "A", Email = "a@x", CreditLimit = 5 } } , Csv,
                new SerializationProfile("Public") { Naming = NamingPolicy.SnakeCase, IgnoreDefaultValues = true });

            Assert.Equal("id,name\r\n1,A\r\n", csv);
        }

        [Fact]
        public void ListsInsideRowsAreRejected()
        {
            var exception = Assert.Throws<SerializationException>(() => ButterflySerializer.SerializeToString(new List<Customer> { new() { Tags = ["x"] } }, Csv));

            Assert.Equal("[0].tag_list", exception.Path);
        }

        [Theory]
        [InlineData("Name,Age\r\nAda\r\n", "[0]")]
        [InlineData("Name,Age\r\nAda,old\r\n", "[0].Age")]
        [InlineData("Name,Age\r\n\"unterminated,1\r\n", "")]
        public void RejectsMalformedCsv(string csv, string path)
        {
            Assert.Equal(path, Assert.Throws<SerializationException>(() => ButterflySerializer.Deserialize<List<Person>>(csv, Csv)).Path);
        }
    }
}
