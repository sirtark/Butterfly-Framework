namespace Butterfly.Communication.Dns
{
    public enum DnsRecordType : ushort
    {
        A = 1,
        NS = 2,
        CNAME = 5,
        SOA = 6,
        PTR = 12,
        MX = 15,
        TXT = 16,
        AAAA = 28,
        SRV = 33,
        OPT = 41,
        CAA = 257,
        ANY = 255
    }

    public enum DnsClass : ushort
    {
        IN = 1,
        CH = 3,
        HS = 4,
        ANY = 255
    }

    public enum DnsResponseCode : byte
    {
        NoError = 0,
        FormatError = 1,
        ServerFailure = 2,
        NameError = 3,
        NotImplemented = 4,
        Refused = 5
    }
}
