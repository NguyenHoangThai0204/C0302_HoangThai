namespace C0302_HoangThai.Service.S0302.IS
{
    public interface IS0302XoaGoiChiDinhInterface
    {
        Task<(bool Success, string Message, object Data)>
            FilterChiTietGoiChiDinh(long IDVaoVien, long IDChiNhanh);
        Task<(bool Success, string Message)> DeleteGoiChiDinh(long IdVaoVien, long IdGoiChiDinh);
    }
}
