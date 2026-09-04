# Inspection 사용 및 조회 계약

## 접속과 화면

기존 서버에서 `http://127.0.0.1:5088/inspect`에 접속한다. `Server:Port` 설정을 공유한다. OpenAI API key 없이도 저장된 데이터를 읽을 수 있다.

| 화면 | 주소 | 표시 |
| --- | --- | --- |
| 기억 목록 | `/inspect` | 내용·ID 검색, depth 필터, 전체 통계, 최근 관계 |
| 기억 상세 | `/inspect/memory/{id}` | 본문, provenance metadata, 직접 부모, outgoing 관계 |
| Trace | `/inspect/trace/{id}` | 조상 기억과 직접 부모 연결, 최종 Source 링크 |
| Source | `/inspect/source/{id}` | 불변 원문, metadata, observation |
| 실행 목록 | `/inspect/runs` | Dream/Meditation의 저장된 기간·상태·시각 |
| 실행 상세 | `/inspect/runs/{id}` | 고정 입력 상한, 예산·비용, work 진행, 출력 기억 |
| Work 상세 | `/inspect/runs/{id}/work?key=...` | 저장된 제안 JSON, 거절 사유, carry의 원본 work 링크 |

화면의 모든 시각은 UTC다. 처리 기간은 시작 포함·끝 제외 `[start, end)`로 표시한다. 기억의 높은 depth는 생성 계층이며 truth·confidence·importance 점수가 아니다.

기억 목록은 본문 앞 240자를 보여준다. 전체 내용은 상세 화면에서 읽는다. 원문은 HTML/Markdown으로 해석하지 않는 일반 텍스트이며 공백·개행을 유지한다. 원문을 읽을 수 없으면 오류 안내와 함께 확인 가능한 metadata와 observation은 계속 표시한다.

## 검색과 페이지

`q`는 내용 또는 ID에 입력 문자열이 그대로 포함되는지 검사한다. 대소문자를 구분하며 `%`, `_`, 따옴표 등은 특수 검색 연산자가 아니다. 검색어 상한은 200자다. SQL `instr`를 사용하며 embedding·recall·유료 API를 호출하지 않는다. 리터럴 부분 문자열 검색은 corpus가 커지면 행 검사가 늘어날 수 있다.

`depth`는 0 이상의 정수이며 생략하면 전체 depth다. 실행의 출력 링크는 `revision` 필터를 사용한다. 목록 검색은 해당 필터와 함께 적용된다.

기억·Source observation·실행·work 목록은 25개씩 SQL `LIMIT/OFFSET`으로 조회한다. 기억과 observation은 `created_at DESC, seq DESC`, 실행은 `started_at DESC, id DESC`, work는 `ordinal, work_key` 순서다.

기억·observation·실행 목록의 첫 요청에서 sequence/ID 상한을 정하고 페이지 링크의 `snapshot`에 유지한다. 그 뒤 추가된 행은 해당 페이지 이동에 끼어들지 않는다. 새 검색이나 ‘최신 상태 보기’로 상한을 갱신한다. 이는 생성 목록의 상한이며 mutable 실행 상태까지 과거 시점으로 동결하는 기능은 아니다. 전체 통계와 최근 관계는 각 요청 시점의 DB를 읽는다.

`p`는 1 이상의 정수다. 잘못된 필터는 400, 없는 저장 ID는 404 안내를 반환한다. 빈 corpus, 검색 결과 없음, 페이지 범위 밖도 빈 목록으로 표시한다.

## Provenance와 관계

Trace는 선택 기억에서 `derived_from`으로 도달하는 조상만 조회한다. 재귀 CTE는 중복 조상을 제거하고 표시 여부 판정을 포함하여 최대 201개 노드를 선택한다. 화면에는 최대 200개가 표시된다. 잘리면 상한 안내가 나타나며 표시되지 않은 직접 부모의 ‘계속 추적’ 링크로 나머지 경로를 읽을 수 있다.

공유 부모를 한 번만 표시해도 각 기억의 직접 부모 링크는 모두 보존한다. 따라서 DAG를 트리처럼 평탄화하여 연결을 잃지 않는다. Source 원문은 Source 링크를 열 때 해당 저장 ID로만 읽는다. 기존 MCP `trace` 구현·반환 계약은 바꾸지 않는다.

관계는 저장된 소유자 → 대상 방향으로만 표시한다. 기억 상세는 그 기억의 outgoing positive/negative와 각 `related_at`을 보여준다. 첫 화면의 최근 관계는 `related_at DESC, seq DESC` 순으로 12개를 표시한다. 대상에서 소유자를 찾는 역방향 필터나 탐색은 제공하지 않는다.

## 실행과 비용의 의미

`finished_at`은 저장된 종료 기록 시각이다. `budget_exhausted`는 완료로 바꾸어 표현하지 않는다. work 초기화 여부는 `work_initialized`로 표시하고 현재 저장된 `complete` work 수와 전체 수를 SQL로 집계한다. 실행 상태와 현재 원본 work 상태는 이월 처리에 따라 서로 다를 수 있다.

실행 비용은 `api_calls.run_id`가 해당 실행인 장부만 읽는다. 금액은 저장된 decimal 문자열을 C# decimal로 합산한다.

- 정산된 실제액: `actual_usd`가 있는 호출의 실제액 합계.
- 미정산 예약액: `actual_usd`가 없는 호출의 `reserved_usd` 합계.
- 예산 차감 합계: 위 두 금액의 합계. 정산된 호출의 과거 예약은 다시 합산하지 않는다.

금액은 설정 단가에 기반한 로컬 장부이며 외부 청구 확정액은 아니다. 0으로 정산된 호출과 미정산 호출을 구분한다.

이월 work는 저장된 `carry:{원래 실행 ID}:{원래 work key}`를 통해 원본 work로 연결한다. 제안·거절·출력 기억의 생성 revision은 원래 실행에 속하고 API 비용은 호출 당시 실행에 청구된다. DB에 없는 work별 비용이나 이월 완료 시각을 추정하지 않는다. 저장된 제안이 있다는 사실만으로 적용 성공이나 완료를 판단하지 않는다.

## 읽기 전용 경계

Razor PageModel은 `IInspectionReader`를 주입받는다. 기존 `SqliteMemoryStore` singleton을 공유하므로 조회용 저장소를 새로 초기화하여 Source 복구를 실행하지 않는다. 각 복합 결과의 조회는 하나의 SQLite 읽기 트랜잭션에서 구성한다. 전역 그래프 캐시를 두지 않는다.

조회는 기억·관계·recall 기록, run/work/usage/state를 바꾸지 않고 cognition이나 scheduler를 호출하지 않는다. 서버 시작의 기존 복구와 백그라운드 scheduler는 별개로 유지된다. 순수한 수동 관찰 실행이 필요하면 기존 `--no-scheduler` 옵션을 사용한다.

기억·원문·제안·거절 사유는 Razor의 자동 HTML 인코딩을 사용한다. Source는 저장 ID로만 조회하며 사용자 파일 경로 입력을 받지 않는다. 오류에는 서버 파일 경로나 상세 예외를 노출하지 않는다. HTML 응답은 `Cache-Control: no-store`를 사용한다.

기존 IPv4 loopback listener와 Host/Origin 검사를 모든 페이지와 로컬 CSS 앞에 적용한다. 정적 CSS는 빌드·배포 출력의 `wwwroot`에서 제공하며 실행 작업 디렉터리에 의존하지 않는다. 별도 프런트엔드 프레임워크·CDN·외부 서비스가 없다.

## 검증

자동 테스트는 실제 SQLite와 가짜 cognition을 사용한다. 목록 순서·상한·검색, DAG·trace 상한, Source 공백·개행·오류, 실행/work 페이지와 decimal 비용 및 carry 소유권을 확인한다. HTTP 조회 전후 전체 도메인 테이블의 내용을 비교하고 cognition 호출 수가 늘지 않는지 검증한다.

HTML 인코딩, 400/404/빈 상태, HTML/CSS의 Host·Origin 보호와 독립 프로세스 서버 시작도 검증한다. 독립 시작을 위해 공유 JSON 옵션에 명시적 type resolver를 설정하며, 기존 snake_case 도구 schema는 유지한다. 실제 OpenAI API는 호출하지 않는다.

### 메인 에이전트 통합 검증

2026-09-04, 실제 corpus와 분리한 표본 35개 기억으로 독립 서버 프로세스와 Chromium을 실행했다. 스케줄러는 비활성화하고 OpenAI API key 없이 검증했다.

- PC 1440px 및 모바일 390px에서 정상·오류 화면 18개를 확인했다. 가로 넘침, 스크립트 실행, 외부 자원 요청은 없었다.
- 페이지 이동과 검색 폼, outgoing 관계 및 역방향 관계의 부재, 공유 부모를 가진 13개 노드의 trace 연결, Source 원문 표시, 실제액·예약액 구분을 확인했다.
- 첫 키보드 포커스의 본문 이동 링크와 화면 배치를 확인했다.
- 조회 전후 SQLite 전체 테이블의 행 수와 내용 해시가 동일했다.
- 서브에이전트 구현을 메인 에이전트가 리뷰했고, 독립 서버 시작 시 JSON resolver 초기화와 원문의 첫 개행 보존 문제를 수정한 뒤 재확인했다.
