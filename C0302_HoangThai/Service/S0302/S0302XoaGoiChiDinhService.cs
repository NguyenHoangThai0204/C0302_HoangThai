
using C0302_HoangThai.Models.M0302;
using C0302_HoangThai.Service.S0302.IS;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory.Database;

namespace C0302_HoangThai.Service.S0302
{
    public class S0302XoaGoiChiDinhService : IS0302XoaGoiChiDinhInterface
    {
        private readonly Context0302 _dbService;
        private readonly ILogger<S0302XoaGoiChiDinhService> _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public S0302XoaGoiChiDinhService(Context0302 dbService,
            ILogger<S0302XoaGoiChiDinhService> logger,
            IHttpContextAccessor httpContextAccessor)
        {
            _dbService = dbService;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
        }

         public async Task<(bool Success, string Message, object Data)> FilterChiTietGoiChiDinh(long IDVaoVien, long IDChiNhanh)
        {
            try
            {
                _logger.LogInformation("FilterChiTietGoiChiDinh - IDVaoVien: {IDVaoVien}, IDChiNhanh: {IDChiNhanh}", IDVaoVien, IDChiNhanh);

                // Ví dụ gọi stored procedure S0302_XuatDanhSachGoiChiDinhCuaBenhNhan
                var data = await _dbService.M0302XoaGoiChiDinhs
                    .FromSqlRaw(
                        "EXEC S0302_XuatDanhSachGoiChiDinhCuaBenhNhan @IdChiNhanh = @p0, @IdVaoVien = @p1",
                        new SqlParameter("@p0", IDChiNhanh),
                        new SqlParameter("@p1", IDVaoVien)
                    )
                    .AsNoTracking()
                    .ToListAsync();
                return (true, "Lấy dữ liệu thành công", data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi FilterChiTietGoiChiDinh");
                return (false, "Có lỗi xảy ra", null);
            }
        }
        public async Task<(bool Success, string Message)> DeleteGoiChiDinh(long IdVaoVien, long IdGoiChiDinh)
        {
            try
            {

                // Lấy connection từ DbContext
                //var connection = _dbService.Database.GetDbConnection();

                //await using var command = connection.CreateCommand();
                //command.CommandText = "EXEC MrVu_XoaGoiChiDinh @IdVaoVien = @p0, @IdGoiChiDinh = @p1";
                //command.CommandType = CommandType.Text;

                //// Thêm parameters
                //var param1 = command.CreateParameter();
                //param1.ParameterName = "@p0";
                //param1.Value = IdVaoVien;
                //param1.DbType = DbType.Int64;

                //var param2 = command.CreateParameter();
                //param2.ParameterName = "@p1";
                //param2.Value = IdGoiChiDinh;
                //param2.DbType = DbType.Int64;

                //command.Parameters.Add(param1);
                //command.Parameters.Add(param2);

                //// Mở connection và execute
                //if (connection.State != ConnectionState.Open)
                //    await connection.OpenAsync();

                //var result = await command.ExecuteNonQueryAsync();

                var result = 1;

                if (result > 0)
                {
                    return (true, "Xóa gói chỉ định thành công");
                }
                else
                {
                    return (false, "Gói chỉ đinh đã được duyệt, không thể xoá");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa gói chỉ định");
                return (false, "Có lỗi xảy ra khi xóa gói chỉ định");
            }
            finally
            {
                // Đảm bảo đóng connection
                if (_dbService.Database.GetDbConnection().State == ConnectionState.Open)
                    await _dbService.Database.GetDbConnection().CloseAsync();
            }
        }

    }
}