document.getElementById("btnShowInfo").addEventListener("click", function () {
    document.getElementById("infoPanel").classList.toggle("d-none");
});


// ============================ Đặt 2 dòng có dấu .,....................... 
document.querySelectorAll('.editable-div').forEach(div => {
    let initialLabel = div.querySelector('.fixed-label');
    if (!initialLabel) {
        div.innerHTML = '<span class="fixed-label" contenteditable="false"></span>';
        initialLabel = div.querySelector('.fixed-label');
    }
    const labelHTML = initialLabel.outerHTML;

    function getLabelEl() {
        return div.querySelector('.fixed-label');
    }

    function ensureTextNodeAfterLabel() {
        const label = getLabelEl();
        if (!label) return null;
        let next = label.nextSibling;
        if (!next || next.nodeType !== Node.TEXT_NODE) {
            next = document.createTextNode('');
            label.parentNode.insertBefore(next, label.nextSibling);
        }
        return next;
    }

    function setCaretAfterLabel() {
        const textNode = ensureTextNodeAfterLabel();
        if (!textNode) return;
        const range = document.createRange();
        range.setStart(textNode, 0);
        range.collapse(true);
        const sel = window.getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
        div.focus();
    }

    function isRangeBeforeOrInsideLabel(range) {
        const label = getLabelEl();
        if (!label || !range) return false;

        if (range.startContainer === div) {
            const labelIndex = Array.prototype.indexOf.call(div.childNodes, label);
            return range.startOffset <= labelIndex;
        }

        let node = range.startContainer;
        while (node && node.parentNode !== div) {
            node = node.parentNode;
        }
        if (!node) return false;
        const index = Array.prototype.indexOf.call(div.childNodes, node);
        if (index === -1) return false;
        const labelIndex = Array.prototype.indexOf.call(div.childNodes, label);
        return index <= labelIndex;
    }

    function ensureCaretNotBeforeLabelOnAction() {
        const sel = window.getSelection();
        if (sel.rangeCount === 0) return;
        const range = sel.getRangeAt(0);
        if (isRangeBeforeOrInsideLabel(range)) setCaretAfterLabel();
    }

    div.addEventListener('keydown', (e) => {
        if (e.key === "Enter") {
            e.preventDefault();
            const sel = window.getSelection();
            const range = sel.getRangeAt(0);
            const br = document.createElement("br");
            range.insertNode(br);

            // Tạo một text node mới để con trỏ nằm ở đó
            const textNode = document.createTextNode("");
            br.parentNode.insertBefore(textNode, br.nextSibling);

            const newRange = document.createRange();
            newRange.setStart(textNode, 0);
            newRange.collapse(true);

            sel.removeAllRanges();
            sel.addRange(newRange);

            ensureCaretNotBeforeLabelOnAction();
        }
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'a') {
            e.preventDefault();
            const sel = window.getSelection();
            const range = document.createRange();
            const label = getLabelEl();
            if (label) {
                range.setStartAfter(label);
                range.setEnd(div, div.childNodes.length);
                sel.removeAllRanges();
                sel.addRange(range);
            }
            return;
        }

        const printable = e.key.length === 1 || e.key === 'Enter' || e.key === 'Tab';
        if (printable && !e.ctrlKey && !e.metaKey && !e.altKey) {
            ensureCaretNotBeforeLabelOnAction();
            return;
        }


        if (e.key === 'Backspace' || e.key === 'Delete') {
            const sel = window.getSelection();
            if (sel.rangeCount > 0) {
                const range = sel.getRangeAt(0);
                const label = getLabelEl();


                if (isRangeBeforeOrInsideLabel(range)) {
                    e.preventDefault();
                    setCaretAfterLabel();
                } else if (label && range.toString().length > 0) {

                    e.preventDefault();
                    div.innerHTML = labelHTML;
                    setCaretAfterLabel();
                }
            }
        }
    });


    ['mouseup', 'click', 'focus'].forEach(evt => {
        div.addEventListener(evt, () => {
            setTimeout(() => {
                const sel = window.getSelection();
                if (sel.rangeCount === 0) return;
                const range = sel.getRangeAt(0);
                if (isRangeBeforeOrInsideLabel(range)) setCaretAfterLabel();
            }, 0);
        });
    });
    div.addEventListener('keypress', function (e) {
        if (e.key === "Enter") {
            e.preventDefault();
            document.execCommand('insertHTML', false, "<br><br>");
        }
    });

    div.addEventListener('input', () => {
        if (!div.querySelector('.fixed-label')) {
            div.innerHTML = labelHTML;
            setCaretAfterLabel();
            return;
        }
        const labels = div.querySelectorAll('.fixed-label');
        if (labels.length > 1) {
            for (let i = 1; i < labels.length; i++) labels[i].remove();
        }
    });

    // PASTE: chèn text chỉ sau label, giữ con trỏ sau phần dán
    div.addEventListener('paste', (e) => {
        e.preventDefault();
        const text = e.clipboardData.getData('text/plain');
        const textNode = ensureTextNodeAfterLabel();
        if (!textNode) return;
        const sel = window.getSelection();
        if (sel.rangeCount > 0) {
            const range = sel.getRangeAt(0);
            if (range.startContainer === textNode) {
                const offset = range.startOffset;
                const old = textNode.nodeValue || '';
                const selLen = range.toString().length;
                textNode.nodeValue = old.slice(0, offset) + text + old.slice(offset + selLen);
                const newRange = document.createRange();
                newRange.setStart(textNode, offset + text.length);
                newRange.collapse(true);
                sel.removeAllRanges();
                sel.addRange(newRange);
            } else {
                textNode.nodeValue = (textNode.nodeValue || '') + text;
                setCaretAfterLabel();
            }
        } else {
            textNode.nodeValue = (textNode.nodeValue || '') + text;
            setCaretAfterLabel();
        }
    });

    div.addEventListener('cut', (e) => {
        const sel = window.getSelection();
        const label = getLabelEl();
        if (label && sel.toString().includes(label.textContent)) e.preventDefault();
    });

    if (!div.querySelector('.fixed-label')) {
        div.innerHTML = labelHTML;
    }
    setTimeout(() => setCaretAfterLabel(), 0);
});
document.querySelectorAll('.editable-div').forEach(div => {
    div.addEventListener('keydown', function (e) {
        if (e.key === "Enter") {
            e.preventDefault();

            const sel = window.getSelection();
            const range = sel.getRangeAt(0);

            // Tạo div mới cho dòng mới
            const newLine = document.createElement("div");
            newLine.style.cssText = `
                border: none;
                outline: none;
                background: transparent;
                font-family: 'Times New Roman', serif;
                font-size: 14px;
                resize: none;
                width: 100%;
                line-height: 20px;
                min-height: 40px;
                padding: 0;
                background-image: repeating-linear-gradient(to right, #000 0%, #000 2px, transparent 2px, transparent 4px), repeating-linear-gradient(to right, #000 0%, #000 2px, transparent 2px, transparent 4px);
                background-repeat: repeat-x, repeat-x;
                background-size: 4px 1px, 4px 1px;
                background-position: 0 19px, 0 39px;
            `;
            newLine.innerHTML = "<br>";

            range.deleteContents();
            range.insertNode(newLine);

            const newRange = document.createRange();
            newRange.setStart(newLine, 0);
            newRange.collapse(true);

            sel.removeAllRanges();
            sel.addRange(newRange);

            ensureCaretNotBeforeLabelOnAction();
        }
    });
});

document.querySelectorAll('input[name="gioiTinh"]').forEach(cb => {
    cb.addEventListener("change", function () {
        if (this.checked) {
            document.querySelectorAll('input[name="gioiTinh"]').forEach(other => {
                if (other !== this) other.checked = false;
            });
        }
    });
});
// ===== Tiền sử gia đình (Có/Không) =====
// ===== Tiền sử gia đình =====
const tsGiaDinh = document.querySelectorAll('input[name="tsGiaDinh"]');
tsGiaDinh.forEach(cb => {
    cb.addEventListener("change", function () {
        if (this.checked) {
            tsGiaDinh.forEach(other => {
                if (other !== this) other.checked = false;
            });
        }
    });
});

// ===== Các bệnh trong bảng =====
const benhRows = document.querySelectorAll("table tbody tr");
benhRows.forEach(row => {
    const checkboxes = row.querySelectorAll('input[type="checkbox"]');

    checkboxes.forEach(cb => {
        cb.addEventListener("change", function () {
            // Nếu checkbox này được chọn
            if (this.checked) {
                // Bỏ chọn tất cả checkbox khác trong cùng dòng
                checkboxes.forEach(other => {
                    if (other !== this) other.checked = false;
                });
            }
        });
    });
});
// Ví dụ dữ liệu JSON (lấy từ server hoặc ajax)
const thongTin = {
    hoTen: "Nguyễn Hoàng Dương Gian Giai Huynh Đệ",
    gioiTinh: "Nam", // "Nam" hoặc "Nữ"
    ngaySinh: "01",
    thangSinh: "01",
    namSinh: "1990",
    tuoi: "35",
    soCMND: "123456789",
    capNgay: { ngay: "15", thang: "08", nam: "2010" },
    noiCap: "Hà Nội",
    choOHienTai: "123 Đường ABC, Quận XYZ, TP.HCM",
    lyDo: "Khám sức khỏe định kỳ",

    // ===== Thêm dữ liệu bệnh án =====
    tienSuGiaDinh: {
        co: true,
        ghiChu: "Tim mạch, tiểu đường"
    },
    tienSuBanThan: {
        0: "Khong", // Bệnh 1
        1: "Co",    // Bệnh 2
        2: "Co",
        5: "Co",    // Bệnh 6
        20: "Khong" // Bệnh 21
    }
};

function loadThongTin(data) {
    // 1. Họ và tên
    const hoTen = document.querySelector('input[type="text"].input-line');
    hoTen.value = data.hoTen;
    hoTen.style.fontWeight = "bold";
    hoTen.style.fontSize = "16px";

    // 2. Giới tính
    const checkboxes = document.querySelectorAll('input[name="gioiTinh"]');
    checkboxes.forEach(cb => cb.checked = false);
    const gioiTinh = [...checkboxes].find(cb => cb.value === data.gioiTinh);
    if (gioiTinh) {
        gioiTinh.checked = true;
        gioiTinh.parentElement.style.fontWeight = "bold";
        gioiTinh.parentElement.style.fontSize = "16px";
    }

    // 3. Ngày tháng năm sinh + tuổi
    const smallInputs = document.querySelectorAll('.form-field:nth-child(3) .small');
    [data.ngaySinh, data.thangSinh, data.namSinh, data.tuoi].forEach((val, idx) => {
        if (smallInputs[idx]) {
            smallInputs[idx].value = val;
            smallInputs[idx].style.fontWeight = "bold";
            smallInputs[idx].style.fontSize = "16px";
        }
    });

    // 4. Số CMND
    const cmnd = document.querySelector('.form-field:nth-child(4) input');
    cmnd.value = data.soCMND;
    cmnd.style.fontWeight = "bold";
    cmnd.style.fontSize = "16px";

    // 5. Cấp ngày / tại
    const capNgayInputs = document.querySelectorAll('.form-field:nth-child(5) .small');
    [data.capNgay.ngay, data.capNgay.thang, data.capNgay.nam].forEach((val, idx) => {
        capNgayInputs[idx].value = val;
        capNgayInputs[idx].style.fontWeight = "bold";
        capNgayInputs[idx].style.fontSize = "16px";
    });

    const noiCap = document.querySelector('.form-field:nth-child(5) .input-line:not(.small)');
    noiCap.value = data.noiCap;
    noiCap.style.fontWeight = "bold";
    noiCap.style.fontSize = "16px";

    // 6. Chỗ ở hiện tại
    const choOHienTai = document.querySelector('.form-field:nth-child(6) textarea');
    choOHienTai.value = data.choOHienTai;
    choOHienTai.style.fontWeight = "bold";
    choOHienTai.style.fontSize = "16px";

    // 7. Lý do
    const lyDo = document.querySelector('.form-field:nth-child(7) input');
    lyDo.value = data.lyDo;
    lyDo.style.fontWeight = "bold";
    lyDo.style.fontSize = "16px";

    // ===== TIỀN SỬ GIA ĐÌNH =====
    const tsGiaDinh = document.querySelectorAll('input[name="tsGiaDinh"]');
    tsGiaDinh.forEach(cb => cb.checked = false);
    if (data.tienSuGiaDinh) {
        const cb = [...tsGiaDinh].find(x =>
            (data.tienSuGiaDinh.co && x.value === "Co") ||
            (!data.tienSuGiaDinh.co && x.value === "Khong")
        );
        if (cb) cb.checked = true;

        const ghiChu = document.querySelector('.form-field input.input-line.medium');
        if (ghiChu) {
            ghiChu.value = data.tienSuGiaDinh.ghiChu || "";
            ghiChu.style.fontWeight = "bold";
            ghiChu.style.fontSize = "16px";
        }
    }

    // ===== TIỀN SỬ BẢN THÂN =====
    if (data.tienSuBanThan) {
        for (let key in data.tienSuBanThan) {
            const value = data.tienSuBanThan[key]; // "Co" hoặc "Khong"
            const selector = `input[name="benh_${key}"][value="${value}"]`;
            const cb = document.querySelector(selector);
            if (cb) cb.checked = true;
        }
    }
}


// Gọi thử
document.addEventListener("DOMContentLoaded", function () {
    loadThongTin(thongTin);
});