// =============================================================================
//  B2S Euresys 샘플 가이드 PDF 생성
//  Puppeteer-core + 시스템 Edge 사용 — Page.printToPDF 의 headerTemplate /
//  footerTemplate 기능으로 매 페이지 반복되는 헤더(로고)/푸터 구현.
// =============================================================================

const puppeteer = require('puppeteer-core');
const fs        = require('fs');
const path      = require('path');

const DOCS_DIR   = path.resolve(__dirname, '..');
const HTML_PATH  = path.join(DOCS_DIR, 'B2S_Euresys_Samples_Guide.html');
const PDF_PATH   = path.join(DOCS_DIR, 'B2S_Euresys_Samples_Guide.pdf');
const LOGO_PATH  = path.join(DOCS_DIR, 'b2s_logo_small.png');

// 시스템 Edge 경로
const EDGE_PATH = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';

(async () => {
  // 로고를 base64 data URI 로 변환 (headerTemplate 에 임베드)
  const logoBuf = fs.readFileSync(LOGO_PATH);
  const logoB64 = logoBuf.toString('base64');
  const logoDataUri = `data:image/png;base64,${logoB64}`;

  // 매 페이지 우상단에 표시될 헤더 (HTML 템플릿)
  // 주의: Puppeteer headerTemplate 안의 모든 텍스트는 기본 폰트 크기가 매우 작음 (1pt 정도)
  // → font-size 명시 필수
  const headerTemplate = `
    <div style="width:100%; padding:0 18mm; box-sizing:border-box; margin-top:8mm;">
      <div style="text-align:right;">
        <img src="${logoDataUri}" style="height:8mm;" />
      </div>
    </div>
  `;

  // 매 페이지 하단 푸터 (자산 안내 + 페이지 번호)
  const footerTemplate = `
    <div style="width:100%; padding:0 18mm; box-sizing:border-box;
                font-family:'Malgun Gothic', sans-serif; font-size:8pt; color:#888;
                display:flex; justify-content:space-between;">
      <span>본 문서는 B2S 의 자산입니다</span>
      <span style="font-family:Consolas, monospace;">
        <span class="pageNumber"></span> / <span class="totalPages"></span>
      </span>
    </div>
  `;

  console.log('Edge 실행 중...');
  const browser = await puppeteer.launch({
    executablePath: EDGE_PATH,
    headless: 'new',
    args: ['--disable-gpu']
  });

  const page = await browser.newPage();

  console.log(`HTML 로드: ${HTML_PATH}`);
  await page.goto('file:///' + HTML_PATH.replace(/\\/g, '/'), {
    waitUntil: 'networkidle0'
  });

  console.log(`PDF 생성: ${PDF_PATH}`);
  await page.pdf({
    path: PDF_PATH,
    format: 'A4',
    printBackground: true,
    displayHeaderFooter: true,
    headerTemplate: headerTemplate,
    footerTemplate: footerTemplate,
    margin: {
      top:    '28mm',
      bottom: '22mm',
      left:   '18mm',
      right:  '18mm'
    },
    // 첫 페이지(표지) 는 헤더/푸터 안 보이게 처리 — HTML 의 .cover 가 페이지 전체를
    // 덮도록 z-index 로 처리되어 있고, 표지는 margin: 0 으로 처리.
    // Puppeteer headerTemplate 은 모든 페이지에 자동 적용되므로 표지에서도 나옴.
    // → 별도 처리 필요할 시 HTML 측 page break 조정으로 해결.
  });

  await browser.close();

  const stats = fs.statSync(PDF_PATH);
  console.log(`완료: ${(stats.size / 1024).toFixed(1)} KB`);
})();
