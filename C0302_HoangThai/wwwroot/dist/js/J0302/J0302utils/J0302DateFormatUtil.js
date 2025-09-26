// goiKham.js - Xử lý ngày tháng, phân trang, xuất báo cáo cho module Gói Khám

// ==================== ĐỊNH DẠNG NGÀY NHẬP ====================
function initDateInputFormatting() {
    const dateInputIds = ["ngayTuNgay", "ngayDenNgay"];

    dateInputIds.forEach(function (id) {
        const input = document.getElementById(id);
        if (!input) return;

        input.addEventListener("input", function () {
            let value = input.value.replace(/\D/g, "");
            let formatted = "";
            let selectionStart = input.selectionStart;

            if (value.length > 0) formatted += value.substring(0, 2);
            if (value.length >= 3) formatted += "-" + value.substring(2, 4);
            if (value.length >= 5) formatted += "-" + value.substring(4, 8);

            if (formatted !== input.value) {
                const prevLength = input.value.length;
                input.value = formatted;
                const newLength = formatted.length;
                const diff = newLength - prevLength;
                input.setSelectionRange(selectionStart + diff, selectionStart + diff);
            }
        });

        input.addEventListener("click", function () {
            const pos = input.selectionStart;
            if (pos <= 2) input.setSelectionRange(0, 2);
            else if (pos <= 5) input.setSelectionRange(3, 5);
            else input.setSelectionRange(6, 10);
        });

        input.addEventListener("keydown", function (e) {
            const pos = input.selectionStart;
            let val = input.value;

            if (e.key === "Backspace" && (pos === 3 || pos === 6)) {
                e.preventDefault();
                input.value = val.slice(0, pos - 1) + val.slice(pos);
                input.setSelectionRange(pos - 1, pos - 1);
            }
            if (e.key === "Delete" && (pos === 2 || pos === 5)) {
                e.preventDefault();
                input.value = val.slice(0, pos) + val.slice(pos + 1);
                input.setSelectionRange(pos, pos);
            }
        });
    });
}

// ==================== DATEPICKER ====================
function initDatePicker() {
    $('[id="ngayTuNgay"], [id="ngayDenNgay"]').datepicker({
        format: 'dd-mm-yyyy',
        autoclose: true,
        language: 'vi',
        todayHighlight: true,
        orientation: 'bottom auto',
        weekStart: 1
    });
}


// ==================== SỰ KIỆN GIAO DIỆN ====================
document.addEventListener('DOMContentLoaded', function () {
    initDatePicker();
    initDateInputFormatting();
});


$(document).ready(function () {
    $('.date-input').datepicker({
        dateFormat: 'dd-mm-yy',
    });

    function parseDate(dateStr) {
        const [day, month, year] = dateStr.split('-').map(Number);
        return new Date(year, month - 1, day);
    }

    function autoAdjustDates(source) {
        const tuNgayStr = $('#ngayTuNgay').val();
        const denNgayStr = $('#ngayDenNgay').val();

        if (tuNgayStr && denNgayStr) {
            try {
                const tuNgay = parseDate(tuNgayStr);
                const denNgay = parseDate(denNgayStr);

                if (tuNgay > denNgay) {
                    if (source === "denNgay") {
                        // Người dùng nhập/chọn đến ngày < từ ngày → chỉnh lại từ ngày
                        $('#ngayTuNgay')
                            .val(denNgayStr)
                            .datepicker('update', denNgayStr)
                            .addClass('highlight-adjust');
                        setTimeout(() => $('#ngayTuNgay').removeClass('highlight-adjust'), 1000);
                    } else if (source === "tuNgay") {
                        // Người dùng nhập/chọn từ ngày > đến ngày → chỉnh lại đến ngày
                        $('#ngayDenNgay')
                            .val(tuNgayStr)
                            .datepicker('update', tuNgayStr)
                            .addClass('highlight-adjust');
                        setTimeout(() => $('#ngayDenNgay').removeClass('highlight-adjust'), 1000);
                    }
                }
            } catch (e) {
                console.warn("Lỗi parseDate:", e);
            }
        }
    }

    // Xử lý khi chọn ngày từ datepicker
    $('#ngayTuNgay').on('changeDate', function () {
        autoAdjustDates("tuNgay");
    });

    $('#ngayDenNgay').on('changeDate', function () {
        autoAdjustDates("denNgay");
    });

    // Xử lý khi nhập tay
    $('#ngayTuNgay').on('input change propertychange paste', function () {
        if ($('#ngayTuNgay').val().length === 10 && $('#ngayDenNgay').val().length === 10) {
            setTimeout(() => autoAdjustDates("tuNgay"), 50);
        }
    });

    $('#ngayDenNgay').on('input change propertychange paste', function () {
        if ($('#ngayTuNgay').val().length === 10 && $('#ngayDenNgay').val().length === 10) {
            setTimeout(() => autoAdjustDates("denNgay"), 50);
        }
    });

    // Khi click icon trigger của datepicker
    $('.datepicker-trigger').click(function () {
        setTimeout(() => {
            // không biết click vào ô nào thì cứ kiểm tra cả hai
            autoAdjustDates("tuNgay");
            autoAdjustDates("denNgay");
        }, 100);
    });
});

$('#selectGiaiDoan').change(function () {
    const selectedValue = $(this).val();
    const container = $('#selectContainer');
    container.empty();

    if (selectedValue === 'Nam' || selectedValue === 'Ngay') {
        container.css('justify-content', 'flex-start');
    } else if (selectedValue === 'Quy' || selectedValue === 'Thang') {
        container.css('justify-content', 'space-around');
    }

    const currentYear = new Date().getFullYear();
    const currentMonth = new Date().getMonth() + 1;
    const currentQuy = Math.ceil(currentMonth / 3);

    // ================== FUNCTION TẠO DROPDOWN ==================
    function createDropdownInput(id, label, values, defaultValue, onSelect, length = 10) {
        const html = `
            <div data-dropdown-wrapper style="width: 45%; position: relative;">
                <label class="form-label">${label}</label>
                <input type="number" class="form-control" id="${id}" value="${defaultValue}" oninput="if(this.value.length > ${length}) this.value = this.value.slice(0, ${length});"  autocomplete="off">
                <div id="${id}Dropdown"
                    style="display:none; position:absolute; top:100%; left:0; width:100%;
                    max-height:200px; overflow-y:auto; z-index:9999; background:white;
                    border:1px solid rgba(0,0,0,.15); border-radius:4px;
                    box-shadow:0 6px 12px rgba(0,0,0,.175);">
                </div>
            </div>
        `;
        container.append(html);

        const $input = $('#' + id);
        const $dropdown = $('#' + id + 'Dropdown');
        let currentHighlightIndex = -1;

        function highlightCurrentItem() {
            const items = $dropdown.find('.dropdown-item');
            items.removeClass('active bg-primary text-white');
            if (currentHighlightIndex >= 0 && currentHighlightIndex < items.length) {
                items.eq(currentHighlightIndex).addClass('active bg-primary text-white');
                const item = items.eq(currentHighlightIndex)[0];
                if (item) item.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            }
        }

        // Trong hàm renderList(), sửa phần kiểm tra giá trị như sau:
        function renderList(filter = '') {
            $dropdown.empty();
            currentHighlightIndex = -1;

            const typedVal = parseInt($input.val(), 10);
            const typedIsAllowed = Number.isFinite(typedVal) && (values.includes(typedVal) || id === 'yearInput');

            // Xác định giá trị hiện tại để highlight
            let highlightVal = typedVal;
            if ((id === 'quyInput' || id === 'thangInput') &&
                (!Number.isFinite(typedVal) ||
                    (id === 'quyInput' && (typedVal < 1 || typedVal > 4)) ||
                    (id === 'thangInput' && (typedVal < 1 || typedVal > 12)))) {

                // Lấy giá trị hiện tại để highlight nhưng không thay đổi input
                const now = new Date();
                if (id === 'quyInput') {
                    highlightVal = Math.ceil((now.getMonth() + 1) / 3);
                } else {
                    highlightVal = now.getMonth() + 1;
                }
            }

            let filteredValues = values.filter(v => !filter || v.toString().includes(filter));
            if (filteredValues.length === 0 && id === 'yearInput') {
                if (Number.isFinite(typedVal)) {
                    filteredValues = [typedVal];
                } else {
                    filteredValues = values.slice();
                }
            } else if (filteredValues.length === 0) {
                filteredValues = values.slice();
            }

            filteredValues.forEach((val, index) => {
                // Sử dụng highlightVal thay vì typedVal để xác định isSelected
                const isSelected = Number.isFinite(highlightVal) && val === highlightVal;
                const item = $(` 
            <a href="#" class="dropdown-item ${isSelected ? 'active bg-primary text-white' : ''}"
               data-val="${val}" data-index="${index}"
               style="padding:8px 16px; display:block; text-decoration:none; color:#333; cursor:pointer;">
               ${val}
            </a>
        `);
                item.on('click', function (e) {
                    e.preventDefault();
                    selectItem(val);
                });
                item.on('mouseenter', function () {
                    currentHighlightIndex = index;
                    highlightCurrentItem();
                });
                $dropdown.append(item);
                if (isSelected) currentHighlightIndex = index;
            });

            const items = $dropdown.find('.dropdown-item');
            if (currentHighlightIndex === -1 && items.length) {
                currentHighlightIndex = 0;
            }
            highlightCurrentItem();
        }

        function selectItem(val) {
            $input.val(val);
            $dropdown.hide();
            if (onSelect) onSelect(val);
        }

        $input.on('focus click', function () {
            renderList();
            $dropdown.show();
        });

        $input.on('input', function () {
            renderList($(this).val());
            $dropdown.show();
        });

        $input.on('keydown', function (e) {
            const items = $dropdown.find('.dropdown-item');
            if (!items.length) return;

            const key = e.key;
            const isUp = key === 'ArrowUp';
            const isDown = key === 'ArrowDown';
            const isEnter = key === 'Enter';
            const isEscape = key === 'Escape';
            const isTab = key === 'Tab';

            if (isUp || isDown || isEnter || isEscape || isTab) e.preventDefault();

            if (isUp) {
                currentHighlightIndex = (currentHighlightIndex <= 0) ? items.length - 1 : currentHighlightIndex - 1;
                highlightCurrentItem();
                return;
            }

            if (isDown) {
                currentHighlightIndex = (currentHighlightIndex >= items.length - 1) ? 0 : currentHighlightIndex + 1;
                highlightCurrentItem();
                return;
            }

            if (isEnter && currentHighlightIndex >= 0) {
                const val = parseInt(items.eq(currentHighlightIndex).data('val'), 10);
                selectItem(val);
                return;
            }

            if (isEscape) {
                $dropdown.hide();
                return;
            }

            if (isTab) {
                if (currentHighlightIndex >= 0) {
                    const val = parseInt(items.eq(currentHighlightIndex).data('val'), 10);
                    selectItem(val);
                }
                return;
            }
        });
        $input.on('keypress', function (e) {
            const invalidChars = ['e', 'E', '+', '-', '.', ','];
            if (invalidChars.includes(e.key)) {
                e.preventDefault();
            }
        });
        $(document).off('click.dropdown-' + id).on('click.dropdown-' + id, function (e) {
            if (!$(e.target).closest('[data-dropdown-wrapper]').length) {
                $dropdown.hide();
            }
        });
    }

    // ================== FORMAT DATE ==================
    function formatDate(date) {
        const day = String(date.getDate()).padStart(2, '0');
        const month = String(date.getMonth() + 1).padStart(2, '0');
        const year = date.getFullYear();
        return `${day}-${month}-${year}`;
    }

    function getMonthDateRange(year, month) {
        const startDate = new Date(year, month - 1, 1);
        const endDate = new Date(year, month, 0);
        return { start: startDate, end: endDate };
    }

    function highlightYearInDropdown(year) {
        $('#yearInputDropdown').find('.dropdown-item').removeClass('active bg-primary text-white');
        const yearItem = $('#yearInputDropdown').find(`[data-val="${year}"]`);
        if (yearItem.length) {
            yearItem.addClass('active bg-primary text-white');
            yearItem[0].scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        }
    }

    // ================== UPDATE DATE RANGE ==================
    function updateDates() {
        let yearRaw = parseInt($('#yearInput').val(), 10);
        let year = Number.isFinite(yearRaw) ? yearRaw : currentYear;

        // Chỉ kiểm tra năm không âm
        if (year < 0 || year > currentYear) {
            year = currentYear;
            $('#yearInput').val(currentYear);
            highlightYearInDropdown(currentYear);
        }

        if (selectedValue === 'Nam') {
            $('#ngayTuNgay').val(`01-01-${year}`);
            $('#ngayDenNgay').val(`31-12-${year}`);
        }
        else if (selectedValue === 'Quy') {
            let quy = parseInt($('#quyInput').val(), 10);
            if (!Number.isFinite(quy)) quy = currentQuy;
            if (quy < 1) quy = 1;
            if (quy > 4) quy = 4;
            $('#quyInput').val(quy);

            const startMonth = (quy - 1) * 3 + 1;
            const endMonth = startMonth + 2;
            $('#ngayTuNgay').val(formatDate(new Date(year, startMonth - 1, 1)));
            $('#ngayDenNgay').val(formatDate(new Date(year, endMonth, 0)));
        }
        else if (selectedValue === 'Thang') {
            let month = parseInt($('#thangInput').val(), 10);
            if (!Number.isFinite(month)) month = currentMonth;
            if (month < 1) month = 1;
            if (month > 12) month = 12;
            $('#thangInput').val(month);

            const { start, end } = getMonthDateRange(year, month);
            $('#ngayTuNgay').val(formatDate(start));
            $('#ngayDenNgay').val(formatDate(end));
        }
        else if (selectedValue === 'Ngay') {
            const today = new Date(Date.now());
            const todayStr = formatDate(today);
            $('#ngayTuNgay').val(todayStr);
            $('#ngayDenNgay').val(todayStr);
        }

        if (selectedValue === 'Nam' || selectedValue === 'Quy' || selectedValue === 'Thang') {
            $('#ngayTuNgay, #ngayDenNgay').prop('disabled', true);
        } else {
            $('#ngayTuNgay, #ngayDenNgay').prop('disabled', false);
        }

        $('#ngayTuNgay').datepicker('setDate', $('#ngayTuNgay').val());
        $('#ngayDenNgay').datepicker('setDate', $('#ngayDenNgay').val());
    }

    const startYear = 2000;
    const yearOptions = Array.from({ length: currentYear - startYear + 1 }, (_, i) => startYear + i);
    createDropdownInput('yearInput', 'Năm', yearOptions, currentYear, updateDates, 4);
    $(document)
        .off('blur', '#yearInput')
        .on('blur', '#yearInput', function () {
            let val = parseInt($(this).val(), 10);
            if (!Number.isFinite(val) || val > currentYear || val < 0) val = currentYear;
            $(this).val(val);

            $('#quyInputDropdown').find('.dropdown-item').removeClass('active bg-primary text-white');
            $('#quyInputDropdown').find(`[data-val="${val}"]`).addClass('active bg-primary text-white');

            updateDates();
        });

    // ================== QUÝ ==================
    if (selectedValue === 'Quy') {
        createDropdownInput('quyInput', 'Quý', [1, 2, 3, 4], currentQuy, updateDates, 1);

        $(document)
            .off('blur', '#quyInput')
            .on('blur', '#quyInput', function () {
                let val = parseInt($(this).val(), 10);
                if (!Number.isFinite(val) || val < 1 || val > 4) val = currentQuy;
                $(this).val(val);

                $('#quyInputDropdown').find('.dropdown-item').removeClass('active bg-primary text-white');
                $('#quyInputDropdown').find(`[data-val="${val}"]`).addClass('active bg-primary text-white');

                updateDates();
            });
    }

    // ================== THÁNG ==================
    else if (selectedValue === 'Thang') {
        createDropdownInput('thangInput', 'Tháng', Array.from({ length: 12 }, (_, i) => i + 1), currentMonth, updateDates, 2);

        $(document)
            .off('blur', '#thangInput')
            .on('blur', '#thangInput', function () {
                let val = parseInt($(this).val(), 10);
                if (!Number.isFinite(val) || val < 1 || val > 12) val = currentMonth;
                $(this).val(val);

                $('#thangInputDropdown').find('.dropdown-item').removeClass('active bg-primary text-white');
                $('#thangInputDropdown').find(`[data-val="${val}"]`).addClass('active bg-primary text-white');

                updateDates();
            });
    }

    else if (selectedValue === 'Ngay') {
        container.empty();
    }

    updateDates();
});


window.addEventListener("load", function () {
    // lấy hàng đầu tiên
    const header1 = document.querySelector(".table-wrapper-scroll thead tr:nth-child(1)");
    if (!header1) return;

    const header1Height = header1.offsetHeight; // chiều cao thật của hàng 1

    // lấy toàn bộ ô của hàng 2
    const header2Cells = document.querySelectorAll(".table-wrapper-scroll thead tr:nth-child(2) th");
    header2Cells.forEach(th => {
        th.style.top = header1Height + "px"; // đặt top đúng bằng chiều cao hàng 1
    });
});
