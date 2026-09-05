## OpenAI API 사용 확정

모든 LLM 기반 semantic processing과 embedding은 **OpenAI API를 직접 사용한다.**

다음 대체 수단은 사용하지 않는다.

- Claude / `claude -p`
- ChatGPT UI 자동화
- 로컬 LLM
- 로컬 embedding model
- 기타 LLM provider

인증은 `OPENAI_API_KEY` 환경변수를 사용하며, API key를 설정 파일이나 저장소에 기록하지 않는다.

텍스트 reasoning은 OpenAI **Responses API**를 사용한다.

초기 기본 모델과 reasoning effort는 다음으로 확정한다.

```text
remember
  model: gpt-5.6-terra
  reasoning_effort: low

recall
  model: gpt-5.6-terra
  reasoning_effort: medium

daily dream
  model: gpt-5.6-terra
  reasoning_effort: high

weekly meditation
  model: gpt-5.6-sol
  reasoning_effort: high

embedding
  model: text-embedding-3-large
```

각 값은 configuration에서 변경 가능하게 구현하되, 위 값을 기본값으로 사용한다.

예:

```text
OpenAI:
  RememberModel: gpt-5.6-terra
  RememberReasoningEffort: low

  RecallModel: gpt-5.6-terra
  RecallReasoningEffort: medium

  DreamModel: gpt-5.6-terra
  DreamReasoningEffort: high

  MeditationModel: gpt-5.6-sol
  MeditationReasoningEffort: high

  EmbeddingModel: text-embedding-3-large
```

LLM 호출 결과 중 구조화된 결과가 필요한 작업은 가능한 한 JSON Schema 기반 structured output을 사용한다.

예:

- `remember`: `0..N` observation proposal
- `recall`: 선택된 Memory ID 목록과 필요한 반환 정보
- Dream Assimilation: positive / negative / unrelated 판단
- Dream Consolidation: exact-ID neighborhood당 `0..1` abstraction proposal과 `derived_from`
- Meditation: reasoning 결과에서 생성할 새로운 Memory proposal

LLM은 DB를 직접 수정하지 않는다.

```text
OpenAI API
    ↓
structured proposal
    ↓
deterministic Core validation
    ↓
SQLite mutation
```

Embedding 역시 OpenAI Embeddings API를 사용하며, embedding model ID를 저장하여 향후 모델 교체와 re-index 여부를 명확히 판단할 수 있게 한다.

모델 교체는 Memory의 truth/confidence에 영향을 주는 개념이 아니다. 모델명은 실행 및 provenance/debugging 정보이며 Memory architecture의 의미론적 invariant가 아니다.
