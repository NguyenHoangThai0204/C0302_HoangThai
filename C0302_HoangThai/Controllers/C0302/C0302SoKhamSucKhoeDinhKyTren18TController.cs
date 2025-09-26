using C0302_HoangThai.Service.S0302.IS;
using Microsoft.AspNetCore.Mvc;

namespace C0302_HoangThai.Controllers.C0302
{
    [Route("so_kham_suc_khoe_dinh_ky_tren_18_tuoi")]
    public class C0302SoKhamSucKhoeDinhKyTren18TController : Controller
    {
        //private string _maChucNang = "/so_kham_suc_khoe_dinh_ky_tren_18_tuoi";
        //private IMemoryCachingServices _memoryCache;

        //private readonly IS0302XoaGoiChiDinhInterface _service;
        //private readonly Context0302 _dbService;

        public C0302SoKhamSucKhoeDinhKyTren18TController(/*IS0302XoaGoiChiDinhInterface service , IMemoryCachingServices memoryCache*/)
        {
            //_service = service;
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


            return View("~/Views/V0302/V0302SoKhamSucKhoeDinhKyTren18T/Index.cshtml");
        }
    }
}
