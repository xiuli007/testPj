import unittest
from types import SimpleNamespace
from unittest.mock import patch

import numpy as np
from opencc import OpenCC

from local_model_worker import build_translation_prompt, clean_translation, process_audio


class LocalModelWorkerTests(unittest.TestCase):
    def test_uses_official_direct_japanese_to_chinese_prompt_shape(self):
        prompt = build_translation_prompt("日本語の文章です。")

        self.assertIn("Translate the following text from Japanese into Chinese:", prompt)
        self.assertIn("Japanese: 日本語の文章です。", prompt)
        self.assertTrue(prompt.endswith("<|im_start|>assistant\n"))

    def test_removes_reasoning_and_converts_result_to_simplified_chinese(self):
        result = clean_translation("<think>reasoning</think>\nChinese: 漢字測試。", OpenCC("t2s"))

        self.assertEqual("汉字测试。", result)

    def test_empty_translation_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "未返回中文"):
            clean_translation("<think>reasoning</think>", OpenCC("t2s"))

    def test_empty_silence_is_nonfatal_and_never_translated(self):
        response = self.process_result("", "", np.zeros(32000, dtype=np.float32))
        self.assertEqual("no_speech", response["outcome"])
        self.assertEqual(0, response["diagnostics"]["rms"])
        self.assertEqual("", response["chinese"])

    def test_empty_energetic_segment_is_not_labelled_silence(self):
        response = self.process_result("", "", np.full(16000, 0.1, dtype=np.float32))
        self.assertEqual("unrecognized", response["outcome"])
        self.assertGreater(response["diagnostics"]["rms"], 0.003)

    def test_recognized_non_japanese_is_not_translated_as_japanese(self):
        response = self.process_result("Hello", "English", np.full(16000, 0.1, dtype=np.float32))
        self.assertEqual("non_japanese", response["outcome"])
        self.assertEqual("", response["japanese"])

    def test_unreported_language_does_not_discard_valid_text(self):
        recognizer = self.recognizer("日本語です。", "")
        with patch("local_model_worker.translate", return_value="这是日语。") as translator:
            response = process_audio(recognizer, None, None, None, np.full(16000, 0.1, dtype=np.float32))
        translator.assert_called_once()
        self.assertEqual("translated", response["outcome"])
        self.assertEqual("日本語です。", response["japanese"])
        self.assertEqual("unreported", response["diagnostics"]["language"])

    def test_real_decode_value_error_is_not_treated_as_silence(self):
        recognizer = self.recognizer("", "")
        recognizer.decode_stream = lambda stream: (_ for _ in ()).throw(ValueError("decoder failed"))
        with self.assertRaisesRegex(ValueError, "decoder failed"):
            process_audio(recognizer, None, None, None, np.zeros(16000, dtype=np.float32))

    def test_translation_value_error_remains_an_explicit_failure(self):
        with patch("local_model_worker.translate", side_effect=ValueError("translation failed")):
            response = process_audio(
                self.recognizer("日本語です。", "Japanese"),
                None, None, None, np.full(16000, 0.1, dtype=np.float32))
        self.assertEqual("error", response["outcome"])
        self.assertEqual("translation", response["stage"])
        self.assertEqual("ValueError", response["error_type"])
        self.assertEqual("日本語です。", response["japanese"])
        self.assertIn("translation failed", response["error"])

    def test_short_fragment_does_not_invoke_decoder_or_translation(self):
        with patch("local_model_worker.translate") as translator:
            response = process_audio(None, None, None, None, np.zeros(7999, dtype=np.float32))
        translator.assert_not_called()
        self.assertEqual("too_short", response["outcome"])

    def process_result(self, text, language, samples):
        with patch("local_model_worker.translate") as translator:
            response = process_audio(self.recognizer(text, language), None, None, None, samples)
        translator.assert_not_called()
        return response

    @staticmethod
    def recognizer(text, language):
        stream = SimpleNamespace(
            result=SimpleNamespace(text=text, lang=language),
            accept_waveform=lambda rate, samples: None)
        return SimpleNamespace(create_stream=lambda: stream, decode_stream=lambda stream: None)


if __name__ == "__main__":
    unittest.main()
