// Cấu hình toastr (thêm ở đầu file)
toastr.options = {
    closeButton: true,
    progressBar: true,
    positionClass: "toast-top-right",
    timeOut: 5000,
    extendedTimeOut: 1000
};

// =====================
// JS modular cho modal
// =====================

const goiTableBody = document.querySelector(".table-gói tbody");
const tbodyRight = document.querySelector("#dichVuTable");
let currentDataTheoGoi = {};
let idVaoVien;
let idGoiChiDinh;

// =====================
// Cập nhật modal header
// =====================
function updateModalTitle(ten, namSinh) {
    const modalTitle = document.getElementById("myModalLabel");
    if (modalTitle) {
        if (ten && namSinh) {
            modalTitle.innerHTML = `
                <div class="patient-header">
                    <span class="patient-icon">👤</span>
                    <span class="patient-details">
                        <span class="patient-name">${ten}</span>
                        <span class="patient-birth">• Năm sinh: ${namSinh}</span>
                    </span>
                </div>
            `;
        } else {
            modalTitle.textContent = "Thông tin";
        }
    }
}

// =====================
// Xóa table
// =====================
function clearTables() {
    if (goiTableBody) goiTableBody.innerHTML = "";
    if (tbodyRight) tbodyRight.innerHTML = "";
    currentDataTheoGoi = {};
}

// =====================
// Render bảng bên trái
// =====================
function renderLeftTable(dataTheoGoi) {
    if (!goiTableBody) return;
    const rowsHtml = Object.keys(dataTheoGoi).map(goi => `
        <tr data-goi="${goi}" style="cursor:pointer;">
            <td class="table-col-75"> <span class="goi-checkmark" style="font-size:16px; color: green !important;"></span>   ${goi}</td>
            <td class="table-col-25">
                <div class="action-buttons">
                    <button class="btn btn-icon btn-outline-danger goi-remove"
                    data-goi="${goi}" 
                    data-bs-toggle="tooltip"
                    data-bs-placement="right"
                    data-id-goi="${dataTheoGoi[goi][0]?.idGoiChiDinh || ''}"
                    data-bs-original-title="Xóa gói">
                        <svg xmlns="http://www.w3.org/2000/svg" class="icon icon-tabler icon-tabler-trash" width="16" height="16" viewBox="0 0 24 24" stroke-width="2" stroke="currentColor" fill="none" stroke-linecap="round" stroke-linejoin="round">
                            <path stroke="none" d="M0 0h24v24H0z" fill="none"></path>
                            <path d="M4 7l16 0"></path>
                            <path d="M10 11l0 6"></path>
                            <path d="M14 11l0 6"></path>
                            <path d="M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2 -2l1 -12"></path>
                            <path d="M9 7v-3a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v3"></path>
                        </svg>
                    </button>
                  
                </div>
            </td>
        </tr>
    `).join('');
    goiTableBody.innerHTML = rowsHtml;
}

// =====================
// Render bảng bên phải
// =====================
function renderDichVuForGoi(goiTen) {
    if (!tbodyRight || !currentDataTheoGoi[goiTen]) return;
    const dichVuHtml = currentDataTheoGoi[goiTen].map(dv => `
        <tr data-goi="${goiTen}">
            <td class="table-col-65">${dv.ten}</td>
            <td class="table-col-15">${dv.soLuong}</td>
            <td class="table-col-20">${formatTienViet(dv.donGia)}</td>
        </tr>
    `).join('');
    tbodyRight.innerHTML += dichVuHtml;
}
// Hàm format số thành tiền Việt
function formatTienViet(number) {
    if (number == null) return '';
    return number.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}


function removeDichVuForGoi(goiTen) {
    if (!tbodyRight) return;
    const rowsToRemove = tbodyRight.querySelectorAll(`tr[data-goi='${goiTen}']`);
    rowsToRemove.forEach(row => row.remove());
}

// =====================
// Group dữ liệu theo gói
// =====================
function groupByGoi(data) {
    const grouped = {};
    data.forEach(item => {
        if (!item?.tenGoiChiDinh) return;
        const goi = item.tenGoiChiDinh;
        if (!grouped[goi]) grouped[goi] = [];
        grouped[goi].push({
            idGoiChiDinh: item.idGoiChiDinh,
            ten: item.tenDichVuKyThuat ?? "",
            soLuong: item.soLuong ?? "",

            donGia: item.donGia ?? ""
        });
    });
    return grouped;
}

// =====================
// Load dữ liệu bệnh nhân
// =====================
function loadBenhNhanData(idVaoVien) {
    if (!idVaoVien) return;

    showLoading(); // bật spinner

    $.post("/goi_chi_dinh_benh_nhan/load_thong_tin",
        { IdChiNhanh: _idcn, IdVaoVien: idVaoVien },
        res => {
            handleResponse(res);
            hideLoading(); // ẩn spinner khi xong
        },
        "json"
    ).fail(err => {
        console.error("Lỗi load dữ liệu:", err);
        hideLoading(); // ẩn spinner khi lỗi
    });
}

// =====================
// Xử lý response
// =====================
function handleResponse(res) {
    if (!res?.success || !Array.isArray(res.data) || res.data.length === 0) {
        clearTables();
        updateModalTitle("", "");
        return;
    }
    // log thử item đầu tiên
    console.log("API item:", res.data);
    clearTables();
    currentDataTheoGoi = groupByGoi(res.data);
    renderLeftTable(currentDataTheoGoi);

    const firstPatient = res.data[0];
    const tenBenhNhan = firstPatient?.tenBenhNhan || "";
    const namSinh = firstPatient?.namSinh || "";
    updateModalTitle(tenBenhNhan, namSinh);
}
let deleteContext = {}; // Lưu dữ liệu để xoá sau khi confirm
// =====================
// Event delegation cho table gói - CHỈ CHO PHÉP CHỌN 1 GÓI
// =====================
if (goiTableBody) {
    goiTableBody.addEventListener('click', function (e) {
        //const row = e.target.closest('tr');
        //if (!row) return;

        //const goiTen = row.dataset.goi;

        //// Nếu click vào nút xóa thì bỏ qua toggle
        //if (e.target.closest('.goi-remove')) {
        //    removeDichVuForGoi(goiTen);
        //    row.classList.remove('active');
        //    const checkmark = row.querySelector('.goi-checkmark');
        //    if (checkmark) checkmark.textContent = '';
        //    return;
        //}

        //// BỎ CHỌN TẤT CẢ CÁC HÀNG KHÁC TRƯỚC KHI CHỌN HÀNG MỚI
        //const allRows = goiTableBody.querySelectorAll('tr');
        //allRows.forEach(otherRow => {
        //    if (otherRow !== row) {
        //        otherRow.classList.remove('active');
        //        const otherCheckmark = otherRow.querySelector('.goi-checkmark');
        //        if (otherCheckmark) {
        //            otherCheckmark.textContent = '';
        //            otherCheckmark.style.color = '';
        //        }
        //    }
        //});

        //// XÓA TẤT CẢ DỊCH VỤ HIỆN CÓ TRONG BẢNG PHẢI
        //if (tbodyRight) tbodyRight.innerHTML = '';

        const row = e.target.closest('tr');
        if (!row) return;

        const goiTen = row.dataset.goi;

        // XỬ LÝ NÚT XOÁ - THÊM return sau khi xử lý
        const btnRemove = e.target.closest('.goi-remove');
        if (btnRemove) {
            e.preventDefault();
            e.stopPropagation(); // QUAN TRỌNG: Ngăn sự kiện lan truyền
            idGoiChiDinh = btnRemove.dataset.idGoi; // Sửa thành dataset.idGoi
        
            // lấy dữ liệu bệnh nhân từ header
            const title = document.getElementById("myModalLabel")?.textContent || "";
            const [tenBenhNhan, namSinh] = title.split(" - ");
            const tuoi = namSinh ? (new Date().getFullYear() - parseInt(namSinh)) : "";

            // gán vào modal
            document.getElementById("deleteTenBenhNhan").textContent = tenBenhNhan || "";
            document.getElementById("deleteTuoi").textContent = tuoi || "";
            document.getElementById("deleteTenGoi").textContent = goiTen || "";

            // lưu context để khi confirm mới xoá
            deleteContext = { row, goiTen };

            const confirmModal = new bootstrap.Modal(document.getElementById('confirmDeleteModal'));
            confirmModal.show();

            return; // QUAN TRỌNG: return sớm để không chạy code phía dưới
        }

        // PHẦN CODE DƯỚI ĐÂY CHỈ CHẠY KHI KHÔNG CLICK NÚT XOÁ
        // BỎ CHỌN TẤT CẢ CÁC HÀNG KHÁC TRƯỚC KHI CHỌN HÀNG MỚI
        const allRows = goiTableBody.querySelectorAll('tr');
        allRows.forEach(otherRow => {
            if (otherRow !== row) {
                otherRow.classList.remove('active');
                const otherCheckmark = otherRow.querySelector('.goi-checkmark');
                if (otherCheckmark) {
                    otherCheckmark.textContent = '';
                    otherCheckmark.style.color = '';
                }
            }
        });

        // XÓA TẤT CẢ DỊCH VỤ HIỆN CÓ TRONG BẢNG PHẢI
        if (tbodyRight) tbodyRight.innerHTML = '';

        // ... tiếp tục code xử lý chọn gói
        // TOGGLE HÀNG HIỆN TẠI (CHỈ CHO PHÉP CHỌN 1)
        const isActive = row.classList.contains('active');

        // Nếu đang active thì bỏ chọn, nếu không thì chọn
        if (isActive) {
            row.classList.remove('active');
            const checkmark = row.querySelector('.goi-checkmark');
            if (checkmark) {
                checkmark.textContent = '';
                checkmark.style.color = 'green';
            }
        } else {
            row.classList.add('active');
            const checkmark = row.querySelector('.goi-checkmark');
            if (checkmark) {
                checkmark.innerHTML = '<i class="ti ti-check" style="font-size:18px; font-weight:bold;"></i>';
                checkmark.style.color = 'green';
            }

            // HIỂN THỊ DỊCH VỤ CHO GÓI ĐƯỢC CHỌN
            if (currentDataTheoGoi[goiTen]) {
                renderDichVuForGoi(goiTen);
            }
        }
    });
}


// =====================
// Gọi khi modal mở
// =====================
const modal = document.getElementById('myModal');
if (modal) {
    modal.addEventListener('show.bs.modal', function () {
       idVaoVien = document.getElementById("idVaoVien").value.trim();
        if (idVaoVien) {
            loadBenhNhanData(idVaoVien);
        }
    });

    modal.addEventListener('hidden.bs.modal', function () {
        idInput.focus();
    });
}
function showLoading() {
    const spinner = document.getElementById("loadingSpinner");
    if (spinner) spinner.classList.remove("d-none");
}

function hideLoading() {
    const spinner = document.getElementById("loadingSpinner");
    if (spinner) spinner.classList.add("d-none");
}

//document.getElementById("btnConfirmDelete")?.addEventListener("click", function () {
//    console.log("Gửi yêu cầu xóa - IDVaoVien: " + idVaoVien + ", IDGoiChiDinh: " + idGoiChiDinh);

//    $.post("/goi_chi_dinh_benh_nhan/delete_goi_chi_dinh",
//        {
//            IdVaoVien: idVaoVien,
//            idGoiChiDinh: idGoiChiDinh
//        },
//        function (res) {
//            try {
//                // Xử lý response JSON
//                if (res.success) {
//                    // Xoá trên UI
//                    removeDichVuForGoi(deleteContext.goiTen);
//                    deleteContext.row.remove();
//                    toastr.success(res.message || "Đã xoá gói " + deleteContext.goiTen);
//                } else {
//                    toastr.error(res.message || "Gói đã được duyệt, không thể xoá");
//                }
//            } catch (error) {
//                console.error("Lỗi xử lý response:", error);
//                toastr.error("Lỗi xử lý dữ liệu");
//            } finally {
//                // LUÔN LUÔN đóng modal và reset context
//                const modal = bootstrap.Modal.getInstance(document.getElementById('confirmDeleteModal'));
//                if (modal) modal.hide();
//                deleteContext = {};
//            }
//        }
//    ).fail(function (xhr, status, error) {
//        console.error("Lỗi AJAX:", status, error);
//        toastr.error("Lỗi kết nối khi xoá gói");

//        // Đảm bảo đóng modal ngay cả khi lỗi
//        const modal = bootstrap.Modal.getInstance(document.getElementById('confirmDeleteModal'));
//        if (modal) modal.hide();
//        deleteContext = {};
//    });
//});
document.getElementById("btnConfirmDelete")?.addEventListener("click", function () {
console.log("Gửi yêu cầu xóa - IDVaoVien: " + idVaoVien + ", IDGoiChiDinh: " + idGoiChiDinh);

$.post("/goi_chi_dinh_benh_nhan/delete_goi_chi_dinh",
    {
        IdVaoVien: idVaoVien,
        idGoiChiDinh: idGoiChiDinh
    },
    function (res) {
        try {
            // Xử lý response JSON
            if (res.success) {
                // Xoá trên UI
                removeDichVuForGoi(deleteContext.goiTen);
                deleteContext.row.remove();

                // Hiển thị thông báo thành công
                toastr.success(res.message || "Đã xoá gói " + deleteContext.goiTen);
            } else {
                // Hiển thị thông báo lỗi
                toastr.error(res.message || "Gói đã được duyệt, không thể xoá");
            }
        } catch (error) {
            console.error("Lỗi xử lý response:", error);
            toastr.error("Lỗi xử lý dữ liệu");
        } finally {
            // LUÔN LUÔN đóng modal và reset context
            const modal = bootstrap.Modal.getInstance(document.getElementById('confirmDeleteModal'));
            if (modal) modal.hide();
            deleteContext = {};
        }
    }
).fail(function (xhr, status, error) {
    console.error("Lỗi AJAX:", status, error);
    toastr.error("Lỗi kết nối khi xoá gói");

    // Đảm bảo đóng modal ngay cả khi lỗi
    const modal = bootstrap.Modal.getInstance(document.getElementById('confirmDeleteModal'));
    if (modal) modal.hide();
    deleteContext = {};
});
});