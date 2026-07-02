# Real manga-ocr run — D:\LocalTranslateHub\outputs\youtube_transcripts\0YF8vecQWYs\source_1080p.mp4
window 48.0s, 15 frames @ 24fps, raw column crops -> manga-ocr (CPU)

## FullRecall  (center_r=20, column_seed count=3, defer=none, allow=none)

frame  t      kind          bbox                       ms    text
1      1      block_merged  (551, 43, 720, 448)               'う\u3000他に何がいる？\u3000以上'
1      1      block_merged  (1208, 123, 1311, 303)            '何がそ\u3000不満'
1      1      broad_split   (1463, 418, 1703, 776)            '視感\u3000．．．\u3000．．．．．．\u3000えー'
1      1      broad_split   (1341, 419, 1452, 774)            '．．．．．．．．．．．．\u3000ぐ\u3000（'
2      2      block_merged  (840, 473, 1006, 749)             '散々ワガ\u3000話'
2      2      block_merged  (208, 337, 412, 786)              'そんなところも\u3000割\u3000嫌いじゃ無い'
2      2      column_seed   (1425, 222, 1485, 478)            'うん．．．．．．'
3      3      broad_split   (1215, 112, 1435, 506)            '．．．．．．。\u3000何がそんな\u3000不満なんだ'
3      3      block_merged  (1529, 459, 1703, 715)            '視感\u3000．．．\u3000．．．．．．'
5      5      column_seed   (1411, 222, 1498, 525)            '．．．\u3000．．．\u3000．．．'
5      5      column_seed   (784, 161, 822, 256)              'これ'
7      7      block_merged  (1225, 123, 1328, 303)            '何がそ\u3000不満'
7      7      column_seed   (859, 545, 903, 792)              '語っといて'
8      8      block_merged  (1481, 465, 1713, 706)            '視聴感\u3000．．．\u3000、'
11     11     block_merged  (1235, 198, 1299, 418)            '不満なんだ'
11     11     broad_split   (1397, 55, 1537, 364)             '．．．．．．\u3000．．．\u3000で、\u3000．．．'
12     12     broad_split   (1240, 125, 1348, 416)            '何がそんな\u3000不満なんだ'
13     13     broad_split   (1243, 125, 1437, 506)            '«\u3000何がそんな\u3000不満なんだ'
13     13     block_merged  (1632, 180, 1700, 313)            '．．．'

**FullRecall: total OCR = 19**
  - KORE(これ): 'これ'
  - KATATTOITE(語っといて): '語っといて'
  - WHITEBOX(何がそんな不満なんだ): '«\u3000何がそんな\u3000不満なんだ'
  - KATATTOITE tail: raw y2=744 -> ocr y2=792  (て must reach y2>=790)
  - eval: {'all_caption_recall': 1.0, 'must_have_recall': 1.0, 'garbage_output_count': 7, 'ocr_calls': 19, 'useful_ocr_per_call': 0.158, 'dropped_by_deferral_count': 0, 'found': ['katattoite', 'kore', 'whitebox'], 'missing_must_have': []}

## Realtime  (center_r=20, column_seed count=3, defer=[(1200, 0, 1920, 1080)], allow=none)

frame  t      kind          bbox                       ms    text
1      1      block_merged  (551, 43, 720, 448)               'う\u3000他に何がいる？\u3000以上'
2      2      block_merged  (840, 473, 1006, 749)             '散々ワガ\u3000話'
2      2      block_merged  (208, 337, 412, 786)              'そんなところも\u3000割\u3000嫌いじゃ無い'
5      5      column_seed   (784, 161, 822, 256)              'これ'
7      7      column_seed   (859, 545, 903, 792)              '語っといて'

**Realtime: total OCR = 5**
  - KORE(これ): 'これ'
  - KATATTOITE(語っといて): '語っといて'
  - WHITEBOX(何がそんな不満なんだ): '<<NOT READ>>'
  - KATATTOITE tail: raw y2=744 -> ocr y2=792  (て must reach y2>=790)
  - eval: {'all_caption_recall': 0.667, 'must_have_recall': 0.667, 'garbage_output_count': 0, 'ocr_calls': 5, 'useful_ocr_per_call': 0.4, 'dropped_by_deferral_count': 46, 'found': ['katattoite', 'kore'], 'missing_must_have': ['whitebox']}

## RealtimeAllow  (center_r=20, column_seed count=3, defer=[(1200, 0, 1920, 1080)], allow=[(1200, 90, 1440, 520)])

frame  t      kind          bbox                       ms    text
1      1      block_merged  (551, 43, 720, 448)               'う\u3000他に何がいる？\u3000以上'
1      1      block_merged  (1208, 123, 1311, 303)            '何がそ\u3000不満'
2      2      block_merged  (840, 473, 1006, 749)             '散々ワガ\u3000話'
2      2      block_merged  (208, 337, 412, 786)              'そんなところも\u3000割\u3000嫌いじゃ無い'
3      3      broad_split   (1215, 112, 1435, 506)            '．．．．．．。\u3000何がそんな\u3000不満なんだ'
5      5      column_seed   (784, 161, 822, 256)              'これ'
7      7      block_merged  (1225, 123, 1328, 303)            '何がそ\u3000不満'
7      7      column_seed   (859, 545, 903, 792)              '語っといて'
11     11     block_merged  (1235, 198, 1299, 418)            '不満なんだ'
12     12     broad_split   (1240, 125, 1348, 416)            '何がそんな\u3000不満なんだ'
13     13     broad_split   (1243, 125, 1437, 506)            '«\u3000何がそんな\u3000不満なんだ'

**RealtimeAllow: total OCR = 11**
  - KORE(これ): 'これ'
  - KATATTOITE(語っといて): '語っといて'
  - WHITEBOX(何がそんな不満なんだ): '«\u3000何がそんな\u3000不満なんだ'
  - KATATTOITE tail: raw y2=744 -> ocr y2=792  (て must reach y2>=790)
  - eval: {'all_caption_recall': 1.0, 'must_have_recall': 1.0, 'garbage_output_count': 0, 'ocr_calls': 11, 'useful_ocr_per_call': 0.273, 'dropped_by_deferral_count': 26, 'found': ['katattoite', 'kore', 'whitebox'], 'missing_must_have': []}
