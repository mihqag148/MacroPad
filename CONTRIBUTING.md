# Quy trình phát triển

## Nhánh riêng cho mọi thay đổi

```bash
git switch main
git pull --ff-only origin main
git switch -c feature/ten-tinh-nang
# Sửa code, kiểm tra và xem lại diff
git status
git diff
git add <cac-file-da-kiem-tra>
git commit -m "Describe the change"
git push -u origin feature/ten-tinh-nang
```

Dùng tiền tố `feature/`, `fix/` hoặc `chore/`. Không commit/push trực tiếp lên main.
Nếu có thay đổi local chưa lưu, xử lý chúng trước khi chuyển nhánh; không tự xóa thay đổi.

## Pull request và kiểm duyệt

1. Mở PR từ nhánh làm việc vào `main`, ghi vấn đề, kết quả và cách kiểm tra.
2. CI build **Firmware ZMK RYNOR ONE** trên mọi PR, kể cả PR chỉ sửa tài liệu. Job `PR checks` chỉ thành công khi toàn bộ job build thành công; lỗi, hủy hoặc bỏ qua build đều không đạt.
3. Xem lại toàn bộ diff. Khi có người cùng làm, nhờ họ review. Người tạo PR không thể tự gửi GitHub Approve cho chính PR của mình.
4. Chủ repo duyệt PR cụ thể trước khi merge. Với trợ lý, chủ repo có thể duyệt bằng tin nhắn nêu rõ PR; build xanh không tự thay thế phê duyệt này.
5. Nếu main có commit mới, cập nhật nhánh bằng merge main (không force-push), chờ CI mới rồi review lại phần thay đổi.

## Bảo vệ main

Cấu hình GitHub cần yêu cầu PR, `PR checks` thành công và nhánh cập nhật với main; chặn force-push/xóa main, áp dụng cả cho admin và yêu cầu giải quyết các hội thoại review.
Không bắt buộc số lượng GitHub Approve khi chưa có reviewer thứ hai; việc chủ repo duyệt trước merge vẫn là yêu cầu làm việc.
Không dùng quyền admin để bỏ qua các kiểm tra.

## Merge và đồng bộ

Sau khi được duyệt và tất cả kiểm tra đạt, merge PR trên GitHub. Xóa nhánh làm việc đã merge, giữ nguyên nhánh `archive/*`.

```bash
git switch main
git pull --ff-only origin main
git fetch --prune
```

Chỉ xóa nhánh local sau khi xác nhận PR đã merge và working tree sạch. Với squash merge, kiểm tra trạng thái PR trước vì Git có thể không nhận nhánh cũ là đã merge.

## CI và phát hành

PR chỉ build/kiểm tra, không có bước phát hành. Push lên main sau merge hoặc chạy thủ công từ main mới có thể phát hành, sau build thành công.
Mỗi phiên bản release đã có được giữ nguyên; muốn phát hành bản mới phải tăng phiên bản. Một thay đổi tài liệu đơn thuần không cần phát hành app/firmware mới.

Build xanh không xác nhận USB/BLE, màn hình hay quá trình nạp trên phần cứng; ghi rõ những kiểm tra thiết bị chưa làm trong PR.

