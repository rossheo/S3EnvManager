// qrcode.min.js(전역 스크립트, App.razor에서 미리 로드됨)가 만드는 전역 QRCode 생성자를 쓴다.
// 시크릿(otpauth:// URI)은 이 안에서만 다뤄지고 어떤 네트워크 요청도 발생시키지 않는다 -
// 렌더링은 전부 브라우저 안에서 canvas/SVG로 그려진다.
customElements.define('qr-code', class extends HTMLElement {
    connectedCallback() {
        const url = this.getAttribute('data-url');
        if (url) {
            // QRCode는 대상 엘리먼트를 비우지 않고 그 안에 그려 넣는다 - Blazor의 향상된
            // 폼 내비게이션이 같은 엘리먼트에 connectedCallback을 다시 태우는 경우(예:
            // 잘못된 인증 코드 제출 후 재렌더링) QR이 겹쳐 그려지는 것을 막는다.
            this.innerHTML = '';
            new QRCode(this, { text: url, width: 200, height: 200 });
        }
    }
});
