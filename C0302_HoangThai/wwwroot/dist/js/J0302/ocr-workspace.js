"use strict";

const loader = document.querySelector("#page-loader");
const ring = loader.querySelector(".loader-ring");
const title = loader.querySelector("#loader-title");
const percentage = loader.querySelector("[data-loader-percent]");
const message = loader.querySelector("[data-loader-message]");
const units = loader.querySelector("[data-loader-units]");
const actions = loader.querySelector("[data-loader-actions]");
const download = loader.querySelector("[data-loader-download]");
const closeButton = loader.querySelector("[data-loader-close]");
let busy = false;

function showPageLoader(percent, detail, count = "") {
    if (!loader.open) loader.showModal();
    ring.style.setProperty("--progress", percent + "%");
    ring.setAttribute("aria-valuenow", percent);
    percentage.textContent = percent + "%";
    if (message.textContent !== detail) message.textContent = detail;
    if (units.textContent !== count) units.textContent = count;
}

loader.addEventListener("cancel", event => {
    if (busy) event.preventDefault();
});
closeButton.addEventListener("click", () => loader.close());

function upload(form) {
    return new Promise((resolve, reject) => {
        const request = new XMLHttpRequest();
        request.open("POST", form.action);
        request.setRequestHeader("X-Pdf-Job", "1");
        request.upload.addEventListener("progress", event => {
            if (!event.lengthComputable) return;
            const uploaded = Math.floor(event.loaded * 100 / event.total);
            showPageLoader(Math.floor(uploaded / 20), "Đang tải tệp lên.", uploaded + "% dung lượng đã gửi");
        });
        request.addEventListener("load", () => {
            if (request.status === 202) {
                try {
                    const result = JSON.parse(request.responseText);
                    if (!result.statusUrl || !result.downloadUrl) throw new Error();
                    resolve(result);
                } catch {
                    reject(new Error("Máy chủ chưa trả về mã xử lý. Vui lòng thử lại."));
                }
                return;
            }

            const plainText = request.getResponseHeader("Content-Type")?.startsWith("text/plain");
            const detail = request.status === 413
                ? "Dung lượng tệp vượt giới hạn tải lên."
                : "Không gửi được tệp. Vui lòng thử lại.";
            reject(new Error(plainText && request.responseText.trim() ? request.responseText.trim() : detail));
        });
        request.addEventListener("error", () => reject(new Error("Mất kết nối trong lúc tải tệp lên.")));
        request.addEventListener("abort", () => reject(new Error("Quá trình tải tệp lên đã dừng.")));
        request.send(new FormData(form));
    });
}

async function waitForResult(statusUrl) {
    let failures = 0;
    let currentPercent = 5;

    while (true) {
        let response;
        let status;
        try {
            response = await fetch(statusUrl, { cache: "no-store", signal: AbortSignal.timeout(10000) });
            if (response.ok) status = await response.json();
        } catch {
            // A network interruption leaves the server job running.
        }
        if (response?.status === 404) {
            throw new Error("Phiên xử lý đã hết hạn hoặc máy chủ vừa khởi động lại. Vui lòng gửi lại tệp.");
        }
        if (!status) {
            failures++;
            if (failures >= 6) throw new Error("Mất kết nối với máy chủ khi đang theo dõi tiến độ.");
            showPageLoader(currentPercent, "Đang kết nối lại với máy chủ.");
            await new Promise(resolve => setTimeout(resolve, Math.min(5000, 1000 * failures)));
            continue;
        }

        failures = 0;
        currentPercent = status.percent;
        const detail = status.total > 0 ? status.completed + " / " + status.total + " đã xử lý" : "";
        showPageLoader(currentPercent, status.message, detail);
        if (status.state === "completed") return;
        if (status.state === "failed") throw new Error(status.message);
        await new Promise(resolve => setTimeout(resolve, 650));
    }
}

for (const form of document.querySelectorAll("[data-pdf-job]")) {
    const submit = form.querySelector("[type=submit]");
    for (const zone of form.querySelectorAll(".file-zone")) {
        const input = zone.querySelector("input");
        const fileLabel = zone.querySelector("[data-file-name]");
        const defaultText = fileLabel.textContent.trim();
        const updateFileLabel = () => {
            const file = input.files[0];
            fileLabel.textContent = file ? file.name + " · " + Math.max(1, Math.round(file.size / 1024)) + " KB" : defaultText;
        };
        input.addEventListener("change", updateFileLabel);
        form.addEventListener("reset", () => setTimeout(updateFileLabel, 0));
        zone.addEventListener("dragover", () => zone.classList.add("is-dragging"));
        for (const event of ["dragleave", "drop"]) {
            zone.addEventListener(event, () => zone.classList.remove("is-dragging"));
        }
    }

    form.addEventListener("submit", async event => {
        if (!loader.showModal) return;
        event.preventDefault();
        if (busy) return;

        busy = true;
        submit.disabled = true;
        loader.dataset.state = "processing";
        title.textContent = "Đang xử lý tài liệu";
        actions.hidden = true;
        download.hidden = true;
        download.removeAttribute("href");
        showPageLoader(0, "Đang tải tệp lên.");

        try {
            const job = await upload(form);
            showPageLoader(5, "Đã tải lên. Đang chờ xử lý.");
            await waitForResult(job.statusUrl);
            loader.dataset.state = "completed";
            title.textContent = "Xử lý hoàn tất";
            download.href = job.downloadUrl;
            download.hidden = false;
        } catch (error) {
            loader.dataset.state = "failed";
            title.textContent = "Không thể hoàn tất";
            showPageLoader(Number(ring.getAttribute("aria-valuenow")), error.message);
        } finally {
            actions.hidden = false;
            (download.hidden ? closeButton : download).focus();
            submit.disabled = false;
            busy = false;
        }
    });
}
