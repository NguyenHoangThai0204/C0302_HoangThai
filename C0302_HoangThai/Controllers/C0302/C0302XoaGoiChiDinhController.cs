using C0302_HoangThai.Service.S0302.IS;
using Microsoft.AspNetCore.Mvc;

namespace DemoCauTruc.Controllers.C0302
{
    [Route("goi_chi_dinh_benh_nhan")]
    public class C0302XoaGoiChiDinhController : Controller
    {
        //private string _maChucNang = "/goi_chi_dinh_benh_nhan";
        //private IMemoryCachingServices _memoryCache;

        private readonly IS0302XoaGoiChiDinhInterface _service;
        //private readonly Context0302 _dbService;
      
        public C0302XoaGoiChiDinhController(IS0302XoaGoiChiDinhInterface service /*, IMemoryCachingServices memoryCache*/)
        {
            _service = service;
            //_memoryCache = memoryCache;
        }

        public async Task<IActionResult> Index()
        {
            //var quyenVaiTro = await _memoryCache.getQuyenVaiTro(_maChucNang);
            //if (quyenVaiTro == null)
            //{
            //    return RedirectToAction("NotFound", "Home");
            //}
            //ViewBag.quyenVaiTro = quyenVaiTro;
            //ViewData["Title"] = CommonServices.toEmptyData(quyenVaiTro);

            ViewBag.quyenVaiTro = new
            {
                Them = true,
                Sua = true,
                Xoa = true,
                Xuat = true,
                CaNhan = true,
                Xem = true,
            };
          

            return View("~/Views/V0302/V0302XoaGoiChiDinh/Index.cshtml");
        }

        [HttpPost("load_thong_tin")]
        public async Task<IActionResult> FilterChiTietGoiChiDinh(long IdVaoVien, long IdChiNhanh)
        {
            var result = await _service.FilterChiTietGoiChiDinh(IdVaoVien , IdChiNhanh);

            if (!result.Success)
            {
                return Json(new { success = false, message = result.Message });
            }

            var dataList = result.Data as IEnumerable<object> ?? new List<object>();

            return Json(new
            {
                success = true,
                message = result.Message,
                data = dataList
            });
        }

        [HttpPost("delete_goi_chi_dinh")]
        public async Task<IActionResult> DeleteGoiChiDinh(long IdVaoVien, long IdGoiChiDinh)
        {
            try
            {
                var result = await _service.DeleteGoiChiDinh(IdVaoVien, IdGoiChiDinh);

                // Chỉ trả về success và message, không có Data
                return Json(new
                {
                    success = result.Success,
                    message = result.Message
                });
            }
            catch (Exception ex)
            {
                // Log lỗi nếu cần
                return Json(new
                {
                    success = false,
                    message = "Có lỗi xảy ra khi xử lý yêu cầu"
                });
            }
        }
    }
}
