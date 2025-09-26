const $inputKho = $("#khoInput");
const $dropdownKho = $("#khoDropdown");
const $hiddenKhoId = $("#khoIdHidden");
let khoIndex = -1;

// Filter danh sách kho
function filterKho(keyword) {
	const lowerKeyword = keyword.toLowerCase();
	const $items = $dropdownKho.find(".kho-item");

	// Sắp xếp
	const itemsArray = $items.get().sort((a, b) => {
		const aName = $(a).data("name");
		const bName = $(b).data("name");
		return aName.localeCompare(bName);
	});

	$dropdownKho.empty().append(itemsArray);

	// Lọc hiển thị
	$items.each(function () {
		const name = $(this).data("name");
		const isVisible = !lowerKeyword || name.includes(lowerKeyword);
		$(this).toggle(isVisible);
	});
}

// Focus input -> show tất cả
$inputKho.on("focus", function () {
	$dropdownKho.show();
	filterKho("");
});

// Gõ vào input
$inputKho.on("input", function () {
	filterKho($(this).val().trim());
});

// Click chọn item
$dropdownKho.on("click", ".kho-item", function () {
	console.log("Clicked item:", $(this).text().trim(), "ID:", $(this).data("id"));
	$inputKho.val($(this).text().trim());
	$hiddenKhoId.val($(this).data("id"));
	$dropdownKho.hide();
});

// Click ngoài thì ẩn
$(document).on("click", function (e) {
	if (!$(e.target).closest("#khoInput, #khoDropdown").length) {
		$dropdownKho.hide();
	}
});

// Điều hướng bằng bàn phím
$inputKho.on("keydown", function (e) {
	if (!$dropdownKho.is(":visible")) return;
	const items = $dropdownKho.find(".kho-item:visible");
	if (items.length === 0) return;

	if (e.key === "ArrowDown") {
		e.preventDefault();
		khoIndex = (khoIndex + 1) % items.length;
		items.removeClass("active").eq(khoIndex).addClass("active");
		items.eq(khoIndex)[0].scrollIntoView({ block: "nearest" });
	} else if (e.key === "ArrowUp") {
		e.preventDefault();
		khoIndex = (khoIndex - 1 + items.length) % items.length;
		items.removeClass("active").eq(khoIndex).addClass("active");
		items.eq(khoIndex)[0].scrollIntoView({ block: "nearest" });
	} else if (e.key === "Enter") {
		e.preventDefault();
		if (khoIndex >= 0 && khoIndex < items.length) {
			items.eq(khoIndex).click();
			khoIndex = -1;
		}
	}
});

$inputKho.on("input click", function () {
	khoIndex = -1;
});

// Sort dropdown theo ABC khi load
$(document).ready(function () {
	filterKho("");
});