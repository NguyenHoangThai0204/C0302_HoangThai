namespace DemoCauTruc.Models.M0302
{
    public class HH_DM_KhoHang
    {
        public long ID { get; set; }
        public string? MaKhoHang { get; set; }
        public string? TenKhoHang { get; set; }

        public bool? KhoTong { get; set; }
        public bool? KhoBHYT { get; set; }
        public bool? KhoDichVu { get; set; }
        public bool? KhoThuocTanDuoc { get; set; }
        public bool? KhoYHCT { get; set; }
        public bool? KhoViThuocYHCT { get; set; }
        public bool? KhoVTYT { get; set; }
        public bool? KhoMau { get; set; }
        public bool? KhoHoaChat { get; set; }
        public bool? KhoDichTruyen { get; set; }
        public bool? KhoVaccine { get; set; }
        public bool? KhoThucPhamChucNang { get; set; }
        public bool? KhoVanPhongPham { get; set; }
        public bool? KhoTuTruc { get; set; }
        public bool? KhoVTYTDB { get; set; }
        public bool? KeToa { get; set; }

        public long? IDChiNhanh { get; set; }
        public string? MaLoaiCLS { get; set; }
        public long? IDKhoa { get; set; }

        public bool Active { get; set; } = true;
    }
}
