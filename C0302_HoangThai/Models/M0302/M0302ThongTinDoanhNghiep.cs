using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DemoCauTruc.Models.M0302
{
    [Table("ThongTinDoanhNghiep")]
    public class M0302ThongTinDoanhNghiep
    {
        [Key]
        public long ID { get; set; }

        public string MaCSKCB { get; set; }
        public string TenCSKCB { get; set; }
        public string DiaChi { get; set; }
        public string DienThoai { get; set; }
        public string Email { get; set; }
        public string? TenCoQuanChuyenMon { get; set; }

        [Column("IDChiNhanh")] // Đảm bảo tên column khớp
        public long IDChiNhanh { get; set; }

        // Thêm các property khác nếu cần
    }
}
