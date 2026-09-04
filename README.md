# Long Journey MCP

C#/.NET 10으로 구현한 공유 기억 서버다. 원문 Source를 Markdown으로 보존하고, SQLite에 기억·단방향 관계·provenance를 쌓는다. 현재 구현 범위는 Phase 1~5(Core, Daily Dream, Weekly Meditation, Inspection, Benchmark)다.

## 실행

.NET 10 SDK와 OpenAI API 사용 권한이 필요하다. 저장소 루트의 PowerShell에서 실행한다.

```powershell
dotnet restore LongJourney.slnx
dotnet build LongJourney.slnx
# 저장소 루트의 key.txt에 API key 한 개를 넣거나 OPENAI_API_KEY 환경변수를 설정한다.
dotnet run --project src/LongJourney.Server -- --Engine:DataDirectory=D:/LongJourneyData
```

MCP 클라이언트의 Streamable HTTP 서버 주소를 `http://127.0.0.1:5088/mcp`로 설정한다. 하나의 서버를 계속 실행하고 여러 클라이언트가 그 서버를 공유한다. 같은 데이터 폴더를 대상으로 서버를 여러 개 실행할 수 없다.

읽기 전용 웹 화면은 `http://127.0.0.1:5088/inspect`다. 기억 검색·상세, provenance 추적, Source 원문, depth 통계·최근 관계, Dream/Meditation 실행·제안·비용을 확인한다. 화면 조회는 API를 호출하거나 recall·작업 상태를 바꾸지 않는다. 백그라운드 작업 없이 관찰하려면 실행 인자에 `--no-scheduler`를 추가한다. 자세한 검색·페이지·비용 계약은 [Inspection 안내](docs/09-inspection.md)를 참고한다.

공개 도구는 `remember(raw)`, `recall(query, context?)`, `trace(memory_id)` 세 개다. 입력 상한 기본값은 4,000 문자이며 하나의 관찰에 가까운 내용을 전달한다. 동일 raw의 재입력은 LLM 호출 전에 중복 처리한다.

Dream은 완료된 날짜마다 생성된 depth 0 기억과 모든 depth의 recall된 기억에서 시작하며 금액 제한이 없다. Meditation은 7일 단위로 depth 1 이상 기억의 생성·relation 추가를 처리한다. **`Engine:MeditationBudgetUsd`를 설정하기 전에는 Meditation이 보류된다.** 실제 N달러 값은 사용자가 정한다. 실행 인자로 `--Engine:MeditationBudgetUsd=원하는금액`을 추가하거나 환경변수 `Engine__MeditationBudgetUsd`에 숫자를 지정한다.

```powershell
dotnet test LongJourney.slnx
```

`dotnet test`는 실제 SQLite와 가짜 cognition/HTTP 응답을 사용한다. 유료 OpenAI 호출이나 실제 계정의 모델 접근 권한을 검증하는 명령은 아니다.

## 문서

- [최초 설계](docs/01-initial-design.md)
- [보완 설계](docs/02-design-supplement.md)
- [Phase 1~3 구현 계획](docs/03-implementation-plan.md)
- [OpenAI API 사용 확정](docs/04-openai-api.md)
- [설정·스케줄·비용·복구 운영 안내](docs/05-operations.md)
- [코드 작성 기준](docs/06-code-style.md)
- [Inspection 화면과 조회 계약](docs/09-inspection.md)

기본 모델은 Remember `gpt-5.6-terra/low`, Recall `gpt-5.6-terra/medium`, Dream `gpt-5.6-terra/high`, Meditation `gpt-5.6-sol/high`, embedding `text-embedding-3-large`다. 모든 운영 semantic processing은 OpenAI Responses/Embeddings API로 수행한다.

## Benchmark

LongMemEval 이력을 재생하여 원문 전체·Remember·Dream·관계·Meditation의 다섯 조건을 비교한다. 실험별 저장소, 중단 후 재개, 답변·채점 분리, 비용 상한과 그래프 검사를 지원한다. 첫 파일럿의 전체 상한은 10달러, 주간 Meditation은 실행당 최대 5달러다. 이 값은 사용 목표가 아니라 상한이다.

실행 순서와 결과 해석은 [Benchmark 설계와 실행](docs/10-benchmark.md)을 참고한다. benchmarks/pilot.json은 한 문항 oracle 기능 확인이고 공식 성능 점수가 아니다.