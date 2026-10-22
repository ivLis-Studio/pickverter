<p align="center">
  <a href="https://ivlis-studio.github.io/pickverter/?lang=ko">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="docs/readme/banner-ko-dark.png">
      <img src="docs/readme/banner-ko-light.png" width="880" alt="Pickverter by ivLis STUDIO — 파일을 고르고 키 하나만 누르면, 커서 바로 옆에서 끝나요.">
    </picture>
  </a>
</p>

<p align="center">
  <a href="https://github.com/ivLis-Studio/pickverter/releases/latest"><img alt="최신 버전" src="https://img.shields.io/github/v/release/ivLis-Studio/pickverter?style=flat-square&label=%EC%B5%9C%EC%8B%A0&color=0A84FF"></a>
  <a href="https://github.com/ivLis-Studio/pickverter/releases"><img alt="다운로드" src="https://img.shields.io/github/downloads/ivLis-Studio/pickverter/total?style=flat-square&label=%EB%8B%A4%EC%9A%B4%EB%A1%9C%EB%93%9C&color=1D1D1F&cacheSeconds=3600"></a>
  <img alt="macOS 14+" src="https://img.shields.io/badge/macOS-14%2B-1D1D1F?style=flat-square&logo=apple&logoColor=white">
  <a href="LICENSE"><img alt="MIT 라이선스" src="https://img.shields.io/badge/license-MIT-1D1D1F?style=flat-square"></a>
  <a href="https://github.com/ivLis-Studio/pickverter/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/ivLis-Studio/pickverter/ci.yml?branch=main&style=flat-square&label=CI"></a>
</p>

<p align="center">
  <a href="https://github.com/ivLis-Studio/pickverter/releases/latest"><b>다운로드</b></a> ·
  <a href="https://ivlis-studio.github.io/pickverter/?lang=ko"><b>웹사이트</b></a> ·
  <a href="#설치">설치</a> ·
  <a href="CHANGELOG.md">변경 내역</a> ·
  <a href="README.md">English</a>
</p>

<!-- 영상은 GitHub 첨부 파일이라 README에서 바로 재생돼요. 원본: promo/ -->
https://github.com/user-attachments/assets/6fce639f-3d08-496d-bed4-327f38d73594

<p align="center"><sub>35초 · 소리 켜고 보세요 · 모든 장면과 소리를 코드로 만들었어요 — <a href="promo/">만든 방법</a></sub></p>

## 사용법

<table>
  <tr>
    <td width="50%" valign="top"><img src="docs/readme/step-ko-1.webp" alt="Finder에서 사진을 고르고 Option을 누르는 모습"><br><b>1 · 파일을 고르고 ⌥ 톡</b><br>Finder에서 파일을 고르고 Option을 톡 — 파일을 ⌥-클릭하거나, 드래그 중에 ⇧ ⌘ ⌃를 눌러도 돼요.</td>
    <td width="50%" valign="top"><img src="docs/readme/step-ko-2.webp" alt="커서 위치에 열린 변환 링, PNG 칸이 선택된 모습"><br><b>2 · 커서 바로 옆에 휠이 열려요</b><br>칸을 가리키세요. 가운데에 클릭하면 무엇이 실행될지 항상 보여줘요.</td>
  </tr>
  <tr>
    <td width="50%" valign="top"><img src="docs/readme/step-ko-3.webp" alt="회전 칸의 옵션이 바깥 링에 펼쳐진 모습"><br><b>3 · 올려 두면 옵션이 펼쳐져요</b><br>옵션이 있는 칸은 바깥 링으로 펼쳐져요 — 회전 방향, 크기, 품질까지.</td>
    <td width="50%" valign="top"><img src="docs/readme/step-ko-4.webp" alt="모서리의 결과 카드와 원본 옆에 생긴 PNG"><br><b>4 · 그 자리에서 완료</b><br>결과는 원본 옆이나 클립보드로 가고, 카드에서 보기·열기·복사·되돌리기를 바로 할 수 있어요.</td>
  </tr>
</table>

## 기능

<table>
  <tr>
    <td valign="top" width="33%"><b>⇧ 변환</b><br><sub>이미지 · 동영상 · 오디오 · PDF · 문서 · EPUB · 오피스·iWork · 표 · 자막 · 압축 파일</sub></td>
    <td valign="top" width="33%"><b>⌘ 도구</b><br><sub>배경 제거 · 얼굴·글자 가리기 · 압축 · 크기 조절 · 자르기 · OCR · 회전 · 구간 자르기 · 나누기·합치기 · PDF 페이지 · 암호 · 정보</sub></td>
    <td valign="top" width="33%"><b>⌃ 액션</b><br><sub>AirDrop · 터미널 · 코드 편집기 · 경로 복사 · 파일 복사 · 이동 · 띄워두기 · 공유 · 체크섬</sub></td>
  </tr>
</table>

- **변환** 이미지(JPG·PNG·HEIC·WebP·AVIF·TIFF·GIF·BMP·PDF·ICO), 동영상(MP4·MOV·WebM·MKV·GIF·MP3·M4A·WAV),
  오디오(MP3·M4A·WAV·FLAC·AIFF·OGG·Opus), PDF(PNG·JPG·TXT·MD·DOCX·RTF·HTML), 문서(PDF·DOCX·RTF·TXT·MD·HTML·ODT·**EPUB**),
  엑셀·Numbers·Pages·Keynote·파워포인트, 표(CSV·TSV·JSON·MD·HTML), 자막(SRT·VTT·TXT), 압축(ZIP·TAR.GZ)
- **편집 창** 동영상·오디오 구간 자르기, 비율 프리셋이 있는 자르기, 가리기(모자이크·흐림·채우기 — 얼굴과 글자 자동 찾기),
  끌어서 정리하는 PDF 페이지, 사진·PDF·미디어 정보 편집(사진은 가능하면 재압축 없이)
- **도구** 압축, 크기 조절, 배경 제거, 텍스트 추출(OCR), 회전·반전, 콜라주, 그림책 EPUB · 동영상 압축·해상도·속도·음소거·스냅샷·나누기·합치기 ·
  오디오 음량 맞추기·무음 자르기·모노·시각화 영상 · PDF 합치기·나누기·쪽 추출·역순·빈 쪽 지우기·암호 · 오피스/iWork 파일 속 이미지 꺼내기 · ZIP/TAR.GZ
- **액션** AirDrop, 터미널·코드 편집기로 열기, 경로 복사(전체·~·이름·셸 따옴표), 클립보드, 최근 폴더로 이동·복사, 항상 위 미리보기, 공유, 체크섬(SHA-256·SHA-1·MD5)

**결과는 원하는 곳으로** — 파일로, 클립보드로, 둘 다, 또는 매번 묻기(설정 › 일반).
⌥-클릭이나 ⌘C는 이번 결과만 클립보드로, ⌘S는 파일로 보내요. 원본은 절대 덮어쓰지 않아요.

**내 맘대로** — 칸을 우클릭해 숨기거나, 옮기거나, 옵션 하나를 메인 링으로 꺼내거나, 클릭했을 때 실행할 동작을 고르세요.

<p align="center">
  <img src="docs/screenshots/wheel-tools.png" width="400" alt="회전 옵션이 펼쳐진 도구 링">
  <img src="docs/screenshots/cards.png" width="400" alt="결과 카드">
</p>

<details>
<summary><b>키보드</b></summary>

| 동작 | |
| --- | --- |
| Finder에서 파일 선택 → **⌥ 톡** | 선택한 파일의 휠 |
| 파일을 **⌥-클릭** | 그 파일의 휠 |
| 파일을 드래그하는 동안 **⇧ / ⌘ / ⌃ 톡** | 변환 / 도구 / 액션 링 — 칸 위에 놓으면 실행 |
| ← → · 1–0 | 휠 이동 · 번호로 칸 실행 |
| ↑ ↓ 또는 바깥으로 이동 | 바깥 링의 옵션 |
| `png`, `mp3`… 타이핑 | 그 형식으로 바로 이동 |
| Tab 또는 ⇧ ⌘ ⌃ | 링 전환 |
| ↩︎ · esc | 실행 · 닫기 |

</details>

## 설치

1. [Releases](https://github.com/ivLis-Studio/pickverter/releases/latest)에서 **Pickverter-x.y.z.dmg**를 내려받아 Pickverter를 응용 프로그램 폴더로 끌어 놓으세요.
2. 아직 공증(notarization)을 받지 않아 처음 한 번은 **시스템 설정 › 개인정보 보호 및 보안**에서 **그래도 열기**를 눌러야 해요(또는 앱 우클릭 › 열기).
3. **손쉬운 사용**(⌥ 탭·드래그 감지)과 **Finder 제어**(선택한 파일 읽기)를 허용하세요. 첫 화면이 차례대로 안내해요.
   이어서 **파일 접근**도 미리 허용할 수 있어요. **전체 디스크 접근 권한**(권장 — 어떤 폴더·드라이브도 다시 묻지 않음)을 켜거나,
   데스크탑·문서·다운로드·iCloud Drive만 허용하세요. 미리 해 두면 작업 도중에 macOS 권한 창이 끼어들지 않아요.
4. 선택: [ffmpeg](https://ffmpeg.org)이 있으면 동영상·오디오 형식이 더 늘어나요 — `brew install ffmpeg` (첫 화면에 설치 버튼이 있어요).
   Pages·Keynote·Numbers나 LibreOffice가 설치되어 있으면 오피스/iWork 문서를 고품질로 내보낼 수 있어요.

macOS 14 이상, Apple 실리콘과 Intel 모두 지원해요.

### 업데이트

하루에 한 번쯤 이 저장소의 릴리스를 확인하고, 클릭 한 번으로(또는 백그라운드에서 받아 두었다가 다음 재시작 때) 설치해요 — 설정 › 업데이트.
설치 전에 Ed25519 서명, 크기와 SHA-256, 번들 ID, 코드 서명을 모두 검증합니다.

지금은 Apple Developer ID 없이 애드혹 서명으로 배포하기 때문에, macOS가 새 버전을 다른 앱으로 보고 업데이트 후 권한(손쉬운 사용·Finder 제어·파일 접근)을 한 번 더 요청해요.
Pickverter가 예전 항목을 정리하고 첫 화면을 다시 열어 바로 켤 수 있게 해 줘요.

### 개인정보

파일은 Mac 밖으로 나가지 않아요. 네트워크 요청은 github.com의 업데이트 확인 하나뿐이고, 끌 수 있어요.

## 언어

English · 한국어 · 日本語 · 简体中文 · 繁體中文 · Español · Français · Deutsch · Русский · Português (Brasil).
설정 › 일반에서 언제든 바꿀 수 있어요. 문구는 모두 `Resources/i18n.json` 한 파일에 있어요. 번역 수정 환영합니다.

## 직접 빌드하기

```sh
scripts/build-app.sh            # release 빌드 → build/Pickverter.app
scripts/build-app.sh debug --run
scripts/test.sh                 # 번역 검사, 휠 자체 테스트, 샘플 파일로 엔진 약 60가지 실행
```

Xcode 프로젝트 없이 `swiftc`로 바로 빌드해요. Command Line Tools만 있으면 SwiftUI 매크로가 필요 없는 SDK를 자동으로 골라요.

로컬 빌드는 애드혹 서명이라, 다시 빌드한 뒤에는 손쉬운 사용 목록에서 Pickverter를 지우고 다시 추가해야 할 수 있어요.
고정 인증서가 있다면 `PICKVERTER_SIGN_IDENTITY`로 서명하세요.

개발용 명령: `build/Pickverter.app/Contents/MacOS/Pickverter cli …` — `list`, `run <option-id> <파일>`, `snapshot`, `editor-snapshot`,
`selftest`, `fixtures`, `inspect`. 편집 창의 답은 환경 변수로 넣을 수 있어서(`PICKVERTER_EDITOR_CROP='{"rect":…}'`) 테스트는 창 없이 돌아가요.

## 릴리스하기

1. `CHANGELOG.md`에 `## [x.y.z]` 섹션을 추가하세요. 릴리스 노트와 업데이트 창의 내용이 돼요.
2. 태그를 푸시하세요: `git tag vx.y.z && git push origin vx.y.z`. **Release** 워크플로가 테스트 → 유니버설 빌드 → DMG·ZIP·서명된 `update.json` 생성 → GitHub 릴리스 게시까지 해요.
   (로컬에서는 `scripts/release.sh x.y.z --publish`)

업데이트 서명 키는 저장소 시크릿 `PICKVERTER_UPDATE_KEY`이고, 공개 키는 `Resources/Info.plist`의 `PVUpdatePublicKey`예요.
이 키로 서명한 업데이트만 설치되니 꼭 백업해 두세요. Developer ID 빌드용 선택 시크릿: `MACOS_CERT_P12`, `MACOS_CERT_PASSWORD`, `MACOS_SIGN_IDENTITY`,
공증용: `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD`.

## 프로젝트 구성

- `Input/` 이벤트 탭(보조 키 탭, ⌥-클릭, 드래그), Finder 선택 읽기
- `Wheel/` 휠 패널, 바깥 옵션 링, 드롭 대상, 렌더링
- `Core/` 옵션 모델, 링별 항목, 휠 꾸미기, 작업 대기열, 현지화
- `Engines/` 이미지(ImageIO, Vision), 미디어(ffmpeg, AVFoundation), PDF(PDFKit), 문서, EPUB, 오피스/iWork, 데이터, 압축
- `UI/` 결과 카드, 편집 창, 설정, 메뉴 막대, 첫 화면, 업데이트 창
- `App/` 앱 생명주기, 환경설정, 업데이터, CLI
- `site/` 소개 사이트 [ivlis-studio.github.io/pickverter](https://ivlis-studio.github.io/pickverter/?lang=ko) (GitHub Pages)
- `promo/` 코드로 렌더링하는 소개 영상 — `promo/make.sh`

## 라이선스

[MIT](LICENSE) © ivLis Studio


