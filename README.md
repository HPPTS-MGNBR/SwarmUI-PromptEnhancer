# SwarmUI Prompt Enhancer

![work of art.jpg](Images/work%20of%20art.jpg)

Most diffusion models come with a LLM text encoder nowadays, 
ComfyUI now has an efficient `Generate Text` node (like ~95% as fast as llama.cpp on optimized models), 
but SwarmUI doesn't yet have a way to use it in Generate tab.

This extension fills this gap, letting you use any LLM from your text encoder directory as a prompt enhancer
from the Generate tab of Swarm. As it relies on Comfy only, you benefits from smart offloading, dynamic vram stuff, etc. 
instead of having to deal with an external inference engine that will then fight with Comfy for your vram.

The default mode is to reuse the diffusion model's text encoder if it's a LLM, saving some time and moving weights around.

System prompts library can be managed with the `Edit` button, some LLMs in Comfy have a weird handling of the 
chat template, so might not work with every model.

`Use Prompt Images` send the reference images of your prompt to the `Generate Text`, can be useful for Edit models.

Default sampling values should be sensible for most models, and are accessible with **Advanced Options** toggled on.

![PE_params.jpg](Images/PE_params.jpg)
*base / advanced params*

## Usage

### During generation

Enable the **Prompt Enhancer** group in the parameters column, set your params, then generate. 

The prompt enhancement happens inside the generation workflow: the generated text is plugged into the prompt encoder node, 
and is saved to the image metadata as `enhanced_prompt` alongside your original `prompt`.

### Pre-Generation

The `✨ PE Enhance` button at the top-left of the prompt box rewrites the prompt in place, without
generating an image. Only the text before the first `<tag>` is rewritten; sections, LoRAs and wildcards after it are kept as-is.
Prompt images are sent too when `Use Prompt Images` is on.

### Captioning

`PE Caption`, in the `More` dropdown next to the active image, describes that image with the text encoder
and replaces the prompt with the description. It uses the built-in `Caption` system prompt, unless you picked
another one than `Default`. Needs a vision-capable encoder (Qwen3-VL, Gemma 3/4, ...).

## Supported models

as of oct. 2026, ping me on Swarm discord if you find others.

### That you may already have as text encoder

- Qwen3
  - 0.6b (Anima)
  - 4b (Z-Image, Klein 4B)
  - 8b (Klein 9B)
- Qwen3-VL
  - 4b (Krea 2)
  - 8b (Ideogram 4)
- Gemma 2 2B (Lumina 2, PixelDiT)
- Gemma 3 12B (LTX-2.x)
- Ministral 3 3B (Ernie)

### Dedicated

- Qwen3.5 family (include 3.6 & 3.8): 0.8B, 2B, 4B, 9B, 27B
- Gemma 4 : E2B, E4B, 12B, 31B

### Not supported

- non-LLM encoders (CLIP, T5, ...)
- Qwen3 32B for MiniMax H3 (the pruned version Comfy shipped & implemented is not capable of generation)
- Qwen2.5 7B for Qwen-Image (misses the output layer)


- Mistral Small 3 24B (Flux2 dev)
- GPT-OSS 20B (Lens)
- LLama3 8B (HiDream)

## Notes

- Negative prompts are never rewritten.
- Sections Swarm appends to the prompt for a given stage (`<base>`, `<refiner>`, `<segment>`) are
  kept out of the enhancement: the global prompt is enhanced once, reused by every stage, and the
  section is appended after it (itself enhanced separately if `Process Sub-Prompts` on).
- The generation seed follows your Wildcard Seed when set, and the image seed otherwise.

## License

Do whatever you want with this, Opus did most of it in the first place.