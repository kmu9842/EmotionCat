"""Offline development probe of official Laya question formatting; synthetic text only."""
import json
import sys
import argparse
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'inference'))
from server import EmotionWorker

parser = argparse.ArgumentParser()
parser.add_argument('--split', choices=['development', 'validation'], default='development')
args = parser.parse_args()
worker = EmotionWorker('multilingual', 2, device='cuda')
worker.load()
if worker.status != 'ready': raise RuntimeError(worker.error)
worker.agent.cfg['head_max_len'] = 384
ids = ['neutral', 'angry', 'love', 'excited', 'sad', 'surprised', 'sleepy', 'confused']
names = ['평온', '화남', '하트', '신남', '슬픔', '놀람', '졸림', '혼란']
concepts = ['무감정', '분노', '사랑', '기쁨', '슬픔', '놀람', '졸림', '혼란']
descriptions = ['감정 없는 일상 대화와 정보 전달', '화남, 짜증, 분노, 불만', '사랑, 애정, 호감', '신남, 기쁨, 행복', '슬픔, 우울, 외로움', '놀람, 예상하지 못한 일', '졸림, 피곤함', '혼란, 이해가 안 됨']
prompt = '글쓴이의 현재 감정을 하나 선택하세요. 감정 표현이 없으면 평온입니다.'
variants = {
    'names': (names, descriptions, prompt),
    'names_bare': (names, [''] * 8, prompt),
    'concepts': (concepts, [''] * 8, '문장에서 드러나는 화자의 감정은 무엇인가요? 감정이 드러나지 않으면 무감정입니다.'),
    'concepts_synonyms': (['무감정', '분노, 화남, 짜증', '사랑, 애정', '기쁨, 행복', '슬픔', '놀람', '졸림', '혼란'], [''] * 8, '문장에서 드러나는 화자의 감정은 무엇인가요? 감정이 드러나지 않으면 무감정입니다.'),
    'concepts_question': (concepts, [''] * 8, '문장을 쓴 사람은 어떤 감정을 느끼고 있나요?'),
    'concepts_english': (concepts, [''] * 8, 'Classify the emotion expressed by the writer. Select no emotion for factual statements, requests, or mentions of an emotion as a topic.'),
}
variants = {k:v for k,v in variants.items() if k.startswith('concepts')}
rows = {k: [] for k in variants}
for case in json.loads(Path('tests/emotion-cases.json').read_text(encoding='utf-8')):
    if case['split'] != args.split: continue
    questions = {k: {'type': 'choice', 'instructions': prompt, 'criteria': dict(zip(keys, descriptions))}
                 for k, (keys, descriptions, prompt) in variants.items()}
    answers = worker.agent.predict(case['text'], questions)['answers']
    for k, answer in answers.items():
        label = ids[variants[k][0].index(answer['choice'])]
        rows[k].append({**case, 'raw': label, 'selected': label if answer['confidence'] >= .35 else 'neutral', 'confidence': answer['confidence'], 'probabilities': answer['probabilities']})
report = []
for k, items in rows.items():
    item = {'variant': k, 'correct': sum(r['selected'] == r['expected'] for r in items), 'total': len(items), 'rows': items}
    report.append(item)
    print(json.dumps({**{key: value for key, value in item.items() if key != 'rows'}, 'misses': [r for r in items if r['selected'] != r['expected']]}, ensure_ascii=True), flush=True)
Path('output/verification/label-format-' + args.split + '.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
