# Phase 4 — Inspection 구현 지시문

## 목표와 현재 상태

Long Journey MCP의 Phase 4를 구현한다. Phase 1~3(Core, Dream, Meditation)과 코드 정리는 구현되어 있다. 기존 로컬 C#/.NET 10 서버에서 기억과 처리 결과를 사람이 확인하는 읽기 전용 웹 화면을 제공한다.

사용자는 이 지시문을 새 서브에이전트에게 전달하여 구현하도록 요청했다. 이전 대화를 전제로 하지 말고 아래 문서와 실제 코드를 읽어 판단한다.

## 먼저 읽을 문서

- docs/06-code-style.md: 코드와 테스트 작성 기준. 가장 먼저 읽는다.
- docs/01-initial-design.md: Phase 4 및 기억·provenance·관찰 도구에 관한 원래 설계.
- docs/02-design-supplement.md: 변경된 설계. 원래 설계와 충돌하면 우선한다.
- docs/04-openai-api.md 및 docs/05-operations.md: 기존 운영 정책.
- README.md, AppHost, MemoryEngine.Trace, IMemoryStore, SqliteMemoryStore.Reads, Models, SqliteSchema, ServerTests.

문서에 남아 있는 “현재 구현은 1~3까지”, “구현 착수 전” 같은 표현은 해당 문서 작성 당시의 범위다. 현재 요청은 Phase 4 구현이다. Phase 5 Benchmark는 이번에 구현하지 않는다.

## 사용자에게 제공할 동작

1. 브라우저에서 http://127.0.0.1:5088/inspect 로 접근한다. 기존 Server:Port 설정을 따른다.
2. 기억 목록: 최신 생성 순서, 안정적인 정렬, 페이지 이동, depth 필터, 내용 또는 ID로 찾을 수 있는 간단한 검색. 검색 의미를 화면에 명확히 표시한다. 이 검색은 OpenAI/embedding/recall을 호출하지 않는다.
3. 기억 상세: 내용, ID, depth, 생성 시각, 최근 recall 시각, 모델, 생성 revision, Source root 수, 직접 부모, 저장된 outgoing positive/negative 관계와 각 related_at. 부모와 관계 대상의 상세 화면으로 이동할 수 있다.
4. Trace: 선택 기억에서 derived_from을 따라 부모와 최종 Source까지 연결을 읽을 수 있는 텍스트 또는 시각적 표현. 공유 부모가 있는 DAG에서도 연결 관계를 잃지 않는다. 큰 그래프에 무조건 전체 corpus를 적재하지 않는다.
5. Source: 저장된 ID를 통해 원문을 읽고 공백·개행을 유지하여 일반 텍스트로 표시한다. Source metadata와 해당 Source의 observation으로 이동할 수 있다. 브라우저의 파일 경로나 임의 파일 읽기를 입력으로 받지 않는다.
6. Depth 통계: 전체 기억·Source·관계 수 및 depth별 기억 수를 간결하게 보여준다. 필요한 집계를 SQL에서 수행한다.
7. Dream/Meditation 실행 목록과 상세: 종류, 기간, 상태, 시작·완료 시각 등 저장된 값, budget과 실제/예약 비용의 정확한 구분, 고정 입력 상한, work 진행 상태, 저장된 제안과 거절 이유를 확인한다. 실행에 속한 출력 기억으로 이동할 수 있어야 한다. 이월 work의 원래 실행과 비용이 청구된 실행을 혼동하지 않는다. DB에 없는 값이나 완료 상태를 추측하여 표시하지 않는다.

첫 화면부터 빈 corpus, 데이터가 있는 상태, 잘못된 필터, 없는 ID, 읽을 수 없는 Source 등 사용자에게 실제로 보이는 상태를 처리한다. 한국어 중심의 간결하고 읽기 쉬운 화면을 만든다. 시각적 계층, 긴 내용 줄바꿈, 키보드 사용, 작은 화면도 고려한다.

## 구현 방향과 경계

- 기존 ASP.NET Core 서버에 Razor Pages 등 C# 중심의 서버 렌더링으로 추가한다. 기본 선택은 Razor Pages와 저장소에 포함된 CSS다. 별도 프런트엔드 프레임워크나 CDN, 새 외부 서비스를 도입할 필요는 없다.
- /mcp와 기존 세 도구 계약은 유지한다. 읽기 전용 화면에서 remember, recall, scheduler, 재시도, 수정, 삭제 등의 실행 기능을 제공하지 않는다.
- 모든 화면 조회는 유료 API 호출, recall 기록, relation 변경, source 복구, run/work/usage 상태 변경을 일으키지 않는다. 기존 서버 시작과 백그라운드 스케줄러의 동작은 별개이며 바꾸지 않는다.
- 기존 loopback listener 및 Host/Origin 검사를 화면과 관련 자원에도 적용한다. 원문, 기억, 모델 제안은 신뢰하지 않는 텍스트로 HTML 인코딩한다. 원문을 HTML/Markdown으로 실행하지 않고, 서버 경로나 비밀값을 오류에 노출하지 않는다.
- Positive/negative 관계의 역방향 조회·탐색은 만들지 않는다. derived_from을 따라 부모로 내려가는 provenance는 허용한다.
- 목록은 DB에서 제한하여 가져오고 일관된 순서로 페이지를 나눈다. 단건·원문별 조회를 위해 전체 그래프나 모든 recall 이벤트를 불러오지 않는다.
- 여러 조회로 구성되는 하나의 결과에는 필요한 일관성을 확보한다. 캐시 수명은 요청 또는 결과의 유효 범위와 맞춘다. 새 장기 전역 캐시를 두지 않는다.
- IReadOnlyList, List, 배열은 실제 호출부의 필요와 소유권을 보고 선택한다. 불필요한 복사와 수동 배열 복사 루프를 만들지 않는다. getter에서 컬렉션이나 Dictionary를 매번 만들지 않는다.
- LINQ는 가급적 피하고 조건문·반복문 본문에는 항상 중괄호를 쓴다. 기능 테스트 통과와 별도로 사람이 흐름·비용·소유권을 이해할 수 있는지 검토한다.
- 기존 불변 조건, 가격/모델, 스케줄, 예산 정책, ingestion 및 검색 동작을 바꾸지 않는다. 필요한 새 조회 DTO와 조회 전용 계약은 기존 코드를 확인한 뒤 가장 단순한 경계로 정한다.
- 최초 설계와 API 확정 문서는 원문 그대로 보존한다.

## 작업 소유권

작업 저장소: D:/Workspace/long-journey-mcp, 현재 브랜치 main.

서브에이전트는 Phase 4의 Core 조회 코드, Server 화면/라우팅/자원, 관련 테스트, README.md, docs/05-operations.md를 소유한다. 필요하면 docs/09-inspection.md에 화면과 쿼리 계약을 기록한다. 메인 에이전트는 이 지시문과 최종 통합 리뷰·검증을 맡는다.

공유 작업 디렉터리다. 다른 작업자의 변경을 되돌리지 말고 변경된 파일을 보고한다. 기존 변경이 있으면 먼저 상태를 확인한다. 서브에이전트는 커밋·푸시하거나 추가 서브에이전트를 만들지 않는다.

## 진행 순서와 검증

1. 먼저 관련 파일을 읽고 이 계획을 리뷰한다. 빠진 요구, 위험, 필요한 파일/테스트, 과한 범위를 간결하게 보고한다. 이 단계에서는 코드를 수정하지 않는다.
2. 메인 에이전트가 계획을 확정하면 직접 구현한다.
3. 실제 SQLite와 가짜 cognition을 활용해 목록 필터·페이지, outgoing 관계, provenance/Source, depth 집계, 실행 비용/상태, 없는 ID와 오류를 검증한다. HTTP 조회 전후 기억/recall/작업/usage 상태를 비교하고 cognition 호출이 없음을 확인하는 의미 있는 회귀 테스트를 포함한다.
4. 화면에서 원문·기억·제안의 HTML 인코딩과 Host/Origin 보호를 검증한다. UI의 빈 상태 및 긴 내용도 확인한다.
5. dotnet format whitespace LongJourney.slnx --no-restore, dotnet format style LongJourney.slnx --no-restore --diagnostics IDE0011 --verify-no-changes, dotnet test LongJourney.slnx --configuration Release --no-restore를 수행한다. 유료 API를 호출하지 않는다.
6. 최종 보고: 변경 파일, 주요 설계 선택과 이유, 검증 결과, 남은 제한. 메인 에이전트가 실제 diff와 브라우저 동작을 리뷰한다.

Git ownership 경고가 있으면 git -c safe.directory=D:/Workspace/long-journey-mcp 명령을 사용한다. Windows 파일 조작은 PowerShell을 일관되게 사용하며 임의의 광범위한 삭제를 하지 않는다.

## 계획 리뷰 후 확정한 세부사항

- Razor Pages, 로컬 CSS, 별도 IInspectionReader를 사용한다. 기존 SqliteMemoryStore singleton을 공유하며 조회용 저장소를 새로 초기화하지 않는다.
- 기억 목록은 기본 25개, created_at DESC와 seq DESC 순서로 표시하고 최초 sequence 상한을 페이지 이동에 유지한다. 내용/ID의 대소문자를 구분하는 리터럴 부분 문자열 검색으로 명시한다.
- 최초 설계 §24의 최근 relations를 포함한다. related_at 최신 순으로 제한된 목록을 읽어 소유자 → 대상, 종류, 생성 시각을 보여준다. 역방향 대상 검색을 추가하지 않는다.
- MemoryEngine.Trace의 전체 snapshot 조회를 UI에서 호출하지 않는다. 조상 범위만 조회하고 DAG의 직접 부모 연결을 보존한다. 표시 상한을 둔다면 잘린 사실과 계속 탐색할 링크를 명확하게 제공하여 일부 결과를 전체처럼 보이지 않게 한다.
- 실행의 finished_at과 work_initialized는 조회 DTO에서 읽는다. 금액 문자열은 C# decimal로 합산하며 실제 정산액, 미정산 예약액, 예산 차감 합계를 구분한다. work별 비용이나 이월 완료 시각 등 기록되지 않은 연결은 추정하지 않는다.
- Source 본문을 읽지 못해도 확인 가능한 metadata와 observation은 유지한다. 상세 예외나 서버 파일 경로는 노출하지 않는다.
- 서브에이전트가 구현을 진행하는 동안 메인 에이전트는 격리된 표본 corpus로 브라우저 검증과 읽기 전용 동작 리뷰를 준비한다. 테스트 기준선은 82개 통과다.
