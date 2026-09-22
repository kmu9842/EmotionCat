"""Compare prompts on synthetic phrases; never inspect live user input."""
import json
import urllib.request
from pathlib import Path

labels = ["neutral", "angry", "love", "excited", "sad", "surprised", "sleepy", "confused"]
korean = ["평온, 특별한 감정 없는 일상 대화나 정보 전달", "화남, 짜증, 분노, 불만", "사랑, 애정, 호감, 고마움", "신남, 기쁨, 행복, 즐거움", "슬픔, 우울, 외로움, 상실", "놀람, 예상치 못한 일에 깜짝 놀람", "졸림, 피곤함, 잠이 옴", "혼란, 이해가 안 됨, 어리둥절함"]
english = ["neutral or no emotion", "anger, irritation, frustration", "love, affection, gratitude", "joy, excitement, happiness", "sadness, grief, loneliness", "surprise, amazement, shock", "sleepiness, fatigue, tiredness", "confusion, uncertainty"]
samples = [
 ("neutral", "내일 회의는 오후 세 시에 시작해요."), ("neutral", "파일은 바탕화면에 저장했습니다."),
 ("angry", "아 진짜 짜증나고 화나네! 너무 열받아!"), ("angry", "제대로 좀 해라 진짜 짜증나게 하네"),
 ("love", "너무 사랑해! 정말 좋아하고 아껴!"), ("love", "너 덕분에 힘이 나. 고마워 사랑해"),
 ("excited", "와 신난다! 너무 행복하고 기뻐!"), ("excited", "드디어 합격했어! 야호 최고야!"),
 ("sad", "너무 슬프고 외로워. 눈물이 나."), ("sad", "정말 속상해. 울고 싶어."),
 ("surprised", "헉 뭐야! 깜짝이야! 정말 놀랐어!"),
 ("sleepy", "하암 너무 졸려. 자고 싶다."), ("confused", "무슨 말인지 이해가 안 돼. 헷갈려."),
 ("neutral", "The meeting starts at three tomorrow."), ("angry", "I am furious! This is so unfair!"),
 ("love", "I love you so much!"), ("excited", "Yay! I am so excited!"), ("sad", "I feel sad and lonely."),
]
prompts = ["Which emotion does the writer express? Choose neutral if the text expresses no clear emotion."]
examples = [
 "No emotion. Ordinary facts, questions or instructions. 예: 파일을 저장했습니다. 회의는 세 시입니다.",
 "Negative anger toward a problem or person. 예: 화나! 짜증나! 열받아! 분노한다! 답답하게 굴지 마!",
 "Love or affection toward someone. 예: 사랑해, 좋아해, 고마워, 소중해.",
 "Positive joy and happiness about something good. 예: 신난다! 행복해! 기뻐! 야호! 합격했어!",
 "Sadness, sorrow, hurt feelings or loneliness. 예: 슬프다, 외롭다, 울고 싶다, 눈물이 나, 속상해.",
 "Surprise at an unexpected event. 예: 헉! 깜짝이야! 놀랐어! 믿을 수가 없어!",
 "Physical sleepiness or fatigue. 예: 졸려, 피곤해, 자고 싶어, 하암.",
 "Not understanding something, confusion. 예: 헷갈려, 모르겠어, 이해가 안 돼, 무슨 말이지?",
]
results = []
for language in ["examples", "examples_korean_ids", "examples_number_ids"]:
 for prompt in prompts:
  option_ids = labels if language != "examples_number_ids" else ["option_" + str(i) for i in range(8)]
  emotions = [{"id": option_ids[i], "name": korean[i].split(',')[0], "description": examples[i]} for i, label in enumerate(labels)]
  if language == "examples_korean_ids":
   for i, e in enumerate(emotions): e["description"] = korean[i].split(',')[0] + ": " + examples[i]
  rows = []
  for expected, text in samples:
   body = json.dumps({"text": text, "emotions": emotions, "instructions": prompt, "source": "test"}).encode()
   req = urllib.request.Request("http://127.0.0.1:47829/classify", data=body, headers={"Content-Type": "application/json", "X-EmotionCat-Token": "emotioncat-local-gpu-test-token-2026"})
   with urllib.request.urlopen(req, timeout=20) as response: r = json.load(response)
   raw_label = labels[option_ids.index(r["emotion"])]
   selected = raw_label if r["confidence"] >= .5 else "neutral"
   rows.append({"expected": expected, "raw": raw_label, "selected": selected, "score": r["confidence"], "ms": r["elapsed_ms"]})
  report = {"criteria": language, "prompt": prompt, "correct": sum(r["selected"] == r["expected"] for r in rows), "total": len(rows), "rows": rows}
  results.append(report)
  print(json.dumps({"criteria": language, "correct": report["correct"], "misses": [r for r in rows if r["selected"] != r["expected"]]}, ensure_ascii=False), flush=True)
Path("output/verification/prompt-examples.json").write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
