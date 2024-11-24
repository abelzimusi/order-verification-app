using System.Text.RegularExpressions;

namespace OrderVerificationAPI.Models
{
    public class Branch
    {
        public int BranchId { get; set; }
        public string Name { get; set; }
        public string PhoneNumber { get; set; }
        public string AdminPhoneNumber { get; set; }
        //public string Group { get; set; }
        public Group Group { get; set; }
    }
    public enum Group
    {
        NJShops,
        Ngundu,
        Chomutobwe,
        TnPAndMunteeInvestments
    }
}
