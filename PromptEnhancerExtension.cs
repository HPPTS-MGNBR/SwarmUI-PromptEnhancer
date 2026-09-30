using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

namespace PromptEnhancer;

/// <summary>Uses the diffusion model's own LLM text encoder (via ComfyUI's 'TextGenerate' node) to expand the prompt before it gets encoded.</summary>
public class PromptEnhancerExtension : Extension
{
    /// <summary>Feature flag, auto-enabled on backends that have the 'TextGenerate' node.</summary>
    public const string Feature = "text_generate";

    /// <summary>Value of <see cref="TextEncoder"/> that means "reuse the encoder the diffusion model already loads".</summary>
    public const string UseModelEncoder = "default";

    public static T2IParamGroup Group;

    public static T2IRegisteredParam<string> TextEncoder, EncoderType, SystemPrompt;

    public static T2IRegisteredParam<int> MaxLength, TopK;

    public static T2IRegisteredParam<double> Temperature, TopP, MinP, RepetitionPenalty;

    public static T2IRegisteredParam<bool> PromptImages, Thinking, SubPrompts;

    public static PermInfo PermEditSystemPrompts = Permissions.Register(new("prompt_enhancer_edit_system_prompts", "[Prompt Enhancer] Edit System Prompts", "Allows the user to create, edit and delete their own Prompt Enhancer system prompts.", PermissionDefault.USER, Permissions.GroupUser));

    /// <summary>Text encoder types the ComfyUI backend supports, from 'CLIPLoader.type'.</summary>
    public static List<string> ComfyEncoderTypes = [];

    /// <summary>Name of the built-in system prompt, and the selected value when the group is first enabled. A user prompt of the same name overrides it.</summary>
    public const string DefaultSystemPrompt = "Default";

    const string DefaultSystemPromptText = """
        You are an expert prompt engineer. Rewrite user prompts to be more descriptive while strictly preserving their core subject and intent.

        Guidelines:
        1. Structure: Keep structured inputs structured (enhance within fields). Convert natural language to detailed paragraphs.
        2. Details: Add concrete visual specifics - form, scale, textures, materials, lighting (quality, direction, color), shadows, spatial relationships, and environmental context.
        3. Text in Images: Put ALL text in quotation marks, matching the prompt's language. Always provide explicit quoted text for objects that would contain text in reality (signs, labels, screens, etc.) - without it, the model generates gibberish.

        Output only the revised prompt and nothing else.
        """;

    /// <summary>Name of the built-in captioning system prompt, used by the 'PE Caption' image button when the user left the selection on <see cref="DefaultSystemPrompt"/>.</summary>
    public const string CaptionSystemPrompt = "Caption";

    const string CaptionSystemPromptText = """
        You are an expert image captioner. Describe the provided image as a detailed text-to-image prompt that would recreate it.

        Guidelines:
        1. Subjects: Describe every main subject precisely - appearance, pose, expression, clothing, and actions.
        2. Scene: Describe the setting, background, and spatial relationships between elements.
        3. Visuals: Describe the medium and style (photo, painting, 3D render, ...), composition and framing, camera angle, lighting (quality, direction, color), color palette, and textures.
        4. Text in Images: Transcribe all visible text exactly, in quotation marks.
        5. Facts only: Describe only what is visible. Never guess or invent details.

        Output a single detailed paragraph, only the description and nothing else.
        """;

    /// <summary>Built-in system prompts, by name. A user prompt of the same name overrides one; deleting it restores the original.</summary>
    static readonly Dictionary<string, string> BuiltinSystemPrompts = new()
    {
        [DefaultSystemPrompt] = DefaultSystemPromptText,
        [CaptionSystemPrompt] = CaptionSystemPromptText
    };

    /// <summary>User generic-data key the user's system prompts are stored under, as a single JSON object of name to text.</summary>
    const string DataName = "prompt_enhancer";

    public override void OnPreInit()
    {
        ScriptFiles.Add("Assets/prompt_enhancer.js");
        StyleSheetFiles.Add("Assets/prompt_enhancer.css");
    }

    public override void OnInit()
    {
        ComfyUIBackendExtension.NodeToFeatureMap["TextGenerate"] = Feature;
        ComfyUIBackendExtension.RawObjectInfoParsers.Add(raw =>
        {
            if (ComfyUIBackendExtension.TryGetRequiredInputs(raw, "CLIPLoader", "type", out JToken types))
            {
                ComfyEncoderTypes = [.. types.Select(t => $"{t}")];
            }
        });
        RegisterParams();
        API.RegisterAPICall(PromptEnhancerListSystemPrompts, false, Permissions.BasicImageGeneration);
        API.RegisterAPICall(PromptEnhancerSaveSystemPrompt, true, PermEditSystemPrompts);
        API.RegisterAPICall(PromptEnhancerDeleteSystemPrompt, true, PermEditSystemPrompts);
        API.RegisterAPICall(PromptEnhancerEnhancePrompt, false, Permissions.BasicImageGeneration);
        API.RegisterAPICall(PromptEnhancerCaptionImage, false, Permissions.BasicImageGeneration);
        // Before LoRAs get applied at -10, so we can drive text generation from the raw text encoder.
        WorkflowGenerator.AddModelGenStep(TrackBaseModel, -11);
        // After every stage (refiner, segments, video, ...) has encoded its prompts, and before post-cleanup at 200.
        WorkflowGenerator.AddStep(Apply, 150);
    }

    /// <summary>Remembers the base model's text encoder as it comes straight out of the loader, since LoRA patches can damage its ability to generate text.</summary>
    static void TrackBaseModel(WorkflowGenerator g)
    {
        if (g.LoadingModelType == "Base" && g.LoadingClip is not null)
        {
            g.NodeHelpers.TryAdd("prompt_enhancer_base_clip", $"{g.LoadingClip[0]}:{g.LoadingClip[1]}");
        }
    }

    void RegisterParams()
    {
        Group = new("Prompt Enhancer", Toggles: true, Open: false);
        TextEncoder = T2IParamTypes.Register<string>(new("[PE] Text Encoder", "Which text encoder generates the enhanced prompt.\n'default' reuses the encoder the selected diffusion model already loads, at no extra VRAM cost - but only works if that encoder is an LLM (Qwen, Gemma, ...).\nOtherwise pick a file from your text encoders folder.",
            UseModelEncoder, Group: Group, FeatureFlag: Feature, OrderPriority: 1, Permission: Permissions.ModelParams, GetValues: EncoderValues
            ));
        SystemPrompt = T2IParamTypes.Register<string>(new("[PE] System Prompt", "Which system prompt instructs the encoder on how to rewrite your prompt.\n'None' sends your prompt through the encoder's own default template.\nUse the 'Edit' button to manage your system prompts.",
            DefaultSystemPrompt, Group: Group, FeatureFlag: Feature, OrderPriority: 2, GetValues: session => ["None", .. SystemPromptsFor(session?.User).Keys.OrderBy(k => k.ToLowerFast())]
            ));
        PromptImages = T2IParamTypes.Register<bool>(new("[PE] Use Prompt Images", "Also show the images attached to your prompt to the encoder, so it can describe and build on them.\nOnly works with vision-capable encoders (Qwen3-VL, Gemma 3/4, ...).\nFollows the 'Text Encoded Image' setting for image sizing.",
            "false", Group: Group, FeatureFlag: Feature, OrderPriority: 3
            ));
        EncoderType = T2IParamTypes.Register<string>(new("[PE] Encoder Type", "Which ComfyUI text encoder type to load an explicitly chosen encoder as.\nLeave off to use the type that matches the current diffusion model.",
            "stable_diffusion", Group: Group, FeatureFlag: Feature, OrderPriority: 4, IsAdvanced: true, Toggleable: true, GetValues: _ => ComfyEncoderTypes
            ));
        MaxLength = T2IParamTypes.Register<int>(new("[PE] Max Length", "Maximum length in tokens of the enhanced prompt.",
            "1024", Min: 1, Max: 32768, Group: Group, FeatureFlag: Feature, OrderPriority: 5, IsAdvanced: true
            ));
        Temperature = T2IParamTypes.Register<double>(new("[PE] Temperature", "Sampling temperature. Higher is more creative and less predictable.",
            "0.7", Min: 0.01, Max: 2, Step: 0.05, Group: Group, FeatureFlag: Feature, OrderPriority: 6, IsAdvanced: true
            ));
        TopP = T2IParamTypes.Register<double>(new("[PE] Top P", "Nucleus sampling cutoff.",
            "0.95", Min: 0, Max: 1, Step: 0.01, Group: Group, FeatureFlag: Feature, OrderPriority: 7, IsAdvanced: true
            ));
        TopK = T2IParamTypes.Register<int>(new("[PE] Top K", "How many candidate tokens to sample from. 0 disables.",
            "64", Min: 0, Max: 1000, Group: Group, FeatureFlag: Feature, OrderPriority: 8, IsAdvanced: true
            ));
        MinP = T2IParamTypes.Register<double>(new("[PE] Min P", "Drops candidate tokens less likely than this fraction of the most likely one. 0 disables.",
            "0.05", Min: 0, Max: 1, Step: 0.01, Group: Group, FeatureFlag: Feature, OrderPriority: 9, IsAdvanced: true
            ));
        RepetitionPenalty = T2IParamTypes.Register<double>(new("[PE] Repetition Penalty", "Penalizes tokens that were already generated. 1 disables.",
            "1.05", Min: 0, Max: 5, Step: 0.01, Group: Group, FeatureFlag: Feature, OrderPriority: 10, IsAdvanced: true
            ));
        Thinking = T2IParamTypes.Register<bool>(new("[PE] Thinking", "Let the encoder reason before answering, if it supports a thinking mode.\nThe reasoning is saved to the image metadata, separately from the enhanced prompt.",
            "false", Group: Group, FeatureFlag: Feature, OrderPriority: 11, IsAdvanced: true
            ));
        SubPrompts = T2IParamTypes.Register<bool>(new("[PE] Process Sub-Prompts", "Also enhance confined sub-prompts (<region:>, <object:>, <segment:>, <refiner>, <video>, ...) - each one independently.\nOff by default, as it means one text generation pass per sub-prompt.",
            "false", Group: Group, FeatureFlag: Feature, OrderPriority: 12, IsAdvanced: true
            ));
    }

    /// <summary>Name markers of the text encoder families ComfyUI can actually run generation on (those whose model class derives from 'BaseGenerate').
    /// Everything else in a text encoders folder is an encoder-only model - T5, CLIP-L/G, LLaMA-3, GPT-OSS - which will error out if asked to generate.
    /// ComfyUI's object_info does not expose this, so name matching is the only cheap signal available.
    /// Used both to filter the encoder dropdown, and to check whether the diffusion model's own encoder files can generate.</summary>
    public static string[] GeneratorNameMarkers = ["gemma", "qwen", "ministral"];

    static bool IsGeneratorName(string name) => GeneratorNameMarkers.Any(name.ToLowerFast().Contains);

    /// <summary>Values for <see cref="TextEncoder"/>: generation-capable models from the user's clip folder.</summary>
    static List<string> EncoderValues(Session session)
    {
        IEnumerable<string> local = Program.T2IModelSets["Clip"].ListModelNamesFor(session).Where(IsGeneratorName);
        return [$"{UseModelEncoder}///Same as the diffusion model", .. local.OrderBy(m => m.ToLowerFast()).Select(m => $"{m}///{T2IParamTypes.CleanModelName(m)}")];
    }

    #region System prompt storage

    /// <summary>The user's own saved system prompts, by name.</summary>
    static JObject StoredSystemPrompts(User user)
    {
        string raw = user?.GetGenericData(DataName, "system_prompts");
        return string.IsNullOrWhiteSpace(raw) ? [] : raw.ParseToJson();
    }

    /// <summary>All system prompts available to the user, by name: the built-in ones, overridden or extended by their own.</summary>
    public static Dictionary<string, string> SystemPromptsFor(User user)
    {
        Dictionary<string, string> result = new(BuiltinSystemPrompts);
        foreach ((string name, JToken text) in StoredSystemPrompts(user))
        {
            result[name] = $"{text}";
        }
        return result;
    }

    static JObject SystemPromptsResponse(User user) => new() { ["prompts"] = JObject.FromObject(SystemPromptsFor(user)), ["builtins"] = JArray.FromObject(BuiltinSystemPrompts.Keys) };

    public static async Task<JObject> PromptEnhancerListSystemPrompts(Session session)
    {
        return SystemPromptsResponse(session.User);
    }

    public static async Task<JObject> PromptEnhancerSaveSystemPrompt(Session session,
        [API.APIParameter("Name of the system prompt.")] string name,
        [API.APIParameter("The system prompt text.")] string text,
        [API.APIParameter("Previous name, when renaming an existing system prompt.")] string oldName = "")
    {
        name = name?.Trim() ?? "";
        // '///' is the dropdown value/label separator, and 'None' is the reserved no-system-prompt value.
        if (name == "" || name == "None" || name.Contains("///"))
        {
            return new() { ["error"] = "Invalid system prompt name." };
        }
        JObject stored = StoredSystemPrompts(session.User);
        if (!string.IsNullOrWhiteSpace(oldName) && oldName != name)
        {
            stored.Remove(oldName);
        }
        stored[name] = text ?? "";
        session.User.SaveGenericData(DataName, "system_prompts", stored.ToString(Newtonsoft.Json.Formatting.None));
        return SystemPromptsResponse(session.User);
    }

    public static async Task<JObject> PromptEnhancerDeleteSystemPrompt(Session session,
        [API.APIParameter("Name of the system prompt to delete. Deleting a built-in one's name restores its original text.")] string name)
    {
        JObject stored = StoredSystemPrompts(session.User);
        stored.Remove(name ?? "");
        session.User.SaveGenericData(DataName, "system_prompts", stored.ToString(Newtonsoft.Json.Formatting.None));
        return SystemPromptsResponse(session.User);
    }

    #endregion

    /// <summary>Enhances the prompt on its own, without generating an image, for the 'Enhance' button above the prompt box.
    /// Only the text before the first '&lt;tag&gt;' is enhanced; the rest (sections, loras, wildcards, ...) is kept as-is after it.</summary>
    public static async Task<JObject> PromptEnhancerEnhancePrompt(Session session,
        [API.APIParameter("Raw mapping of the generate tab's current parameters, same format as 'GenerateText2Image'.")] JObject rawInput)
    {
        T2IParamInput input = T2IAPI.RequestToParams(session, rawInput);
        string prompt = input.Get(T2IParamTypes.Prompt, "");
        int tagStart = prompt.IndexOf('<');
        string head = (tagStart < 0 ? prompt : prompt[..tagStart]).Trim();
        string tail = tagStart < 0 ? "" : prompt[tagStart..].Trim();
        if (head == "")
        {
            return new() { ["error"] = "Nothing to enhance: the prompt has no plain text before its first <tag>." };
        }
        // Parameters are left out of the input when the group is toggled off, so fall back to the built-in system prompt rather than none.
        if (!input.TryGet(SystemPrompt, out string _))
        {
            input.Set(SystemPrompt, DefaultSystemPrompt);
        }
        string enhanced = await RunStandalone(input, head);
        return new() { ["prompt"] = tail == "" ? enhanced : $"{enhanced}\n{tail}" };
    }

    /// <summary>Describes an image as a prompt, for the 'PE Caption' image button.
    /// Uses the built-in captioning system prompt, unless the user picked a system prompt other than <see cref="DefaultSystemPrompt"/>.</summary>
    public static async Task<JObject> PromptEnhancerCaptionImage(Session session,
        [API.APIParameter("Raw mapping of the generate tab's current parameters, same format as 'GenerateText2Image', plus 'pe_caption_image': the image to caption, as a data URL.")] JObject rawInput)
    {
        string imageData = $"{rawInput["pe_caption_image"]}";
        rawInput.Remove("pe_caption_image");
        if (!imageData.StartsWith("data:image/"))
        {
            return new() { ["error"] = "No image to caption." };
        }
        T2IParamInput input = T2IAPI.RequestToParams(session, rawInput);
        if (input.Get(SystemPrompt, DefaultSystemPrompt) == DefaultSystemPrompt)
        {
            input.Set(SystemPrompt, CaptionSystemPrompt);
        }
        // The captioned image is the only image the encoder sees, whatever is attached to the prompt box.
        input.Set(T2IParamTypes.PromptImages, [(Image)ImageFile.FromDataString(imageData)]);
        input.Set(PromptImages, true);
        return new() { ["prompt"] = await RunStandalone(input, "Describe this image.") };
    }

    /// <summary>Runs a single text generation outside of any image generation: loads the model's encoder exactly as a generation would, then runs only the 'TextGenerate' node (Comfy skips the loader nodes nothing depends on).</summary>
    static async Task<string> RunStandalone(T2IParamInput input, string prompt)
    {
        ComfyUIAPIAbstractBackend backend = ComfyUIBackendExtension.RunningComfyBackends.FirstOrDefault(b => b.SupportedFeatures.Contains(Feature))
            ?? throw new SwarmUserErrorException("No running ComfyUI backend supports the 'TextGenerate' node.");
        T2IModel model = input.Get(T2IParamTypes.Model, null) ?? throw new SwarmUserErrorException("No model selected.");
        // A random seed per click when the seed is left at -1, so re-clicking gives a new result.
        input.LockSeeds();
        WorkflowGenerator g = new() { UserInput = input, ModelFolderFormat = backend.ModelFolderFormat, Features = [.. backend.SupportedFeatures], Workflow = [] };
        g.FinalLoadedModel = model;
        g.FinalLoadedModelList = [model];
        (g.FinalLoadedModel, g.CurrentModel, g.CurrentTextEnc, g.CurrentVae) = g.CreateModelLoader(model, "Base", "4", sectionId: T2IParamInput.SectionID_BaseOnly);
        Enhance(g, Prepare(g), prompt, "enhanced_prompt");
        await backend.AwaitJobLive(g.Workflow.ToString(), "0", _ => { }, input, Program.GlobalProgramCancel);
        if (input.ExtraMeta.GetValueOrDefault("custom_enhanced_prompt") is not string text || string.IsNullOrWhiteSpace(text))
        {
            throw new SwarmUserErrorException("The text encoder returned no text.");
        }
        return text;
    }

    /// <summary>Everything a 'TextGenerate' node needs beyond the prompt itself, resolved once per generation.</summary>
    public record class Setup(JArray Clip, JObject Inputs);

    /// <summary>Enhances the global prompt wherever it got encoded, and optionally every sub-prompt, each as its own independent generation.
    /// Sections that Swarm appends to the global prompt for a given stage (&lt;base&gt;, &lt;refiner&gt;, ...) are kept separate from it: the global prompt is enhanced once and reused, the section is appended after.</summary>
    static void Apply(WorkflowGenerator g)
    {
        // The whole group toggles at once, so any one parameter being present means the feature is on.
        if (!g.UserInput.TryGet(TextEncoder, out string _))
        {
            return;
        }
        PromptRegion regions = new(g.UserInput.Get(T2IParamTypes.Prompt, ""));
        string global = regions.GlobalPrompt.Trim();
        HashSet<string> suffixes = NonEmpty([regions.BasePrompt, regions.RefinerPrompt, regions.PixelDecoderPrompt]);
        HashSet<string> subPrompts = g.UserInput.Get(SubPrompts, false)
            ? NonEmpty([.. regions.Parts.Where(p => p.Type != PromptRegion.PartType.ClearSegment).Select(p => p.Prompt), regions.BackgroundPrompt, regions.VideoPrompt, regions.VideoSwapPrompt, .. suffixes])
            : [];
        Setup setup = null;
        Dictionary<string, JArray> enhanced = [];
        int subIndex = 0;
        JArray enhance(string text)
        {
            if (!enhanced.TryGetValue(text, out JArray result))
            {
                setup ??= Prepare(g);
                result = Enhance(g, setup, text, text == global ? "enhanced_prompt" : $"enhanced_subprompt_{LetterIndex(subIndex++)}");
                enhanced[text] = result;
            }
            return result;
        }
        // Encode nodes are created with dynamic IDs across many workflow steps, so match them by their text instead. Snapshot, since enhancing adds nodes.
        foreach (JToken node in g.Workflow.Values().ToArray())
        {
            if (!TryGetPromptInput(node, out JObject inputs, out string textKey, out string text))
            {
                continue;
            }
            text = text.Trim();
            if (text == "")
            {
                continue;
            }
            if (text == global)
            {
                inputs[textKey] = enhance(global);
            }
            // Swarm joins them as "{global} {section}", but the global text keeps whatever whitespace (eg a newline) preceded the section tag, so only match on the trimmed parts.
            else if (global != "" && text.StartsWith(global) && suffixes.Contains(text[global.Length..].Trim()))
            {
                string suffix = text[global.Length..].Trim();
                string joined = g.CreateNode("StringConcatenate", new JObject()
                {
                    ["string_a"] = enhance(global),
                    ["string_b"] = subPrompts.Contains(suffix) ? enhance(suffix) : suffix,
                    ["delimiter"] = " "
                });
                inputs[textKey] = new JArray(joined, 0);
            }
            else if (subPrompts.Contains(text))
            {
                inputs[textKey] = enhance(text);
            }
        }
    }

    /// <summary>0 = "a", 25 = "z", 26 = "aa", ... Metadata keys get reduced to their letters when Swarm builds output paths, so numbered keys would collide there.</summary>
    static string LetterIndex(int index) => index < 26 ? $"{(char)('a' + index)}" : LetterIndex(index / 26 - 1) + (char)('a' + index % 26);

    static HashSet<string> NonEmpty(IEnumerable<string> texts) => [.. texts.Select(t => t?.Trim()).Where(t => !string.IsNullOrEmpty(t))];

    /// <summary>Gets the raw prompt text out of a text encode node, if it has one that hasn't already been replaced by a link.</summary>
    static bool TryGetPromptInput(JToken node, out JObject inputs, out string textKey, out string text)
    {
        (inputs, textKey, text) = (null, null, null);
        if (node is not JObject obj || obj["inputs"] is not JObject nodeInputs)
        {
            return false;
        }
        textKey = nodeInputs.ContainsKey("prompt") ? "prompt" : "text";
        if (nodeInputs[textKey] is not JValue val || val.Type != JTokenType.String)
        {
            return false;
        }
        (inputs, text) = (nodeInputs, $"{val}");
        return true;
    }

    /// <summary>Follows 'clip' inputs upstream from the given text encoder output, to the node that loads the encoder files. Null if it isn't a separate encoder loader (eg a checkpoint).</summary>
    static JObject FindClipLoader(WorkflowGenerator g, JArray clip)
    {
        for (int depth = 0; depth < 32 && clip is not null; depth++)
        {
            if (g.Workflow[$"{clip[0]}"] is not JObject node || node["inputs"] is not JObject inputs)
            {
                return null;
            }
            if (inputs.Properties().Any(p => p.Name.StartsWith("clip_name")))
            {
                return inputs;
            }
            clip = inputs["clip"] as JArray;
        }
        return null;
    }

    /// <summary>Resolves the encoder, system prompt, and sampling values for this generation.</summary>
    static Setup Prepare(WorkflowGenerator g)
    {
        // The encoder straight off the loader - the one in CurrentTextEnc has LoRAs patched into it, which damages text generation.
        JArray baseClip = g.NodeHelpers.TryGetValue("prompt_enhancer_base_clip", out string baseClipRef) ? [baseClipRef.Before(':'), int.Parse(baseClipRef.After(':'))] : g.CurrentTextEnc.Path;
        JObject baseLoader = FindClipLoader(g, baseClip);
        string encoderChoice = g.UserInput.Get(TextEncoder, UseModelEncoder);
        JArray clip;
        if (encoderChoice == UseModelEncoder)
        {
            bool isGenerator = baseLoader is not null && baseLoader.Properties().Any(p => p.Name.StartsWith("clip_name") && IsGeneratorName($"{p.Value}"));
            if (!isGenerator)
            {
                throw new SwarmUserErrorException("Prompt Enhancer: the selected model's text encoder is not an LLM and cannot generate text. Pick an explicit encoder in '[PE] Text Encoder'.");
            }
            clip = baseClip;
        }
        else
        {
            string loader = g.CreateNode("CLIPLoader", new JObject()
            {
                ["clip_name"] = encoderChoice,
                // Load it the same way the diffusion model loads its own encoder, unless told otherwise.
                ["type"] = g.UserInput.Get(EncoderType, null) ?? $"{baseLoader?["type"] ?? "stable_diffusion"}"
            });
            clip = [loader, 0];
        }
        string choice = g.UserInput.Get(SystemPrompt, "None");
        string system = choice == "None" ? null : SystemPromptsFor(g.UserInput.SourceSession?.User).GetValueOrDefault(choice)?.Trim();
        if (string.IsNullOrWhiteSpace(system))
        {
            system = null;
        }
        // The wildcard seed when the user set one, the image seed otherwise. Never negative - Comfy rejects -1 here.
        // (Not T2IParamInput.GetWildcardSeed(), which has the side effect of writing the wildcard seed back onto the user input.)
        long seed = g.UserInput.TryGet(T2IParamTypes.WildcardSeed, out long wildcardSeed) && wildcardSeed >= 0 ? wildcardSeed : g.UserInput.Get(T2IParamTypes.Seed, 0);
        // Inputs nested in the dynamic 'sampling_mode' combo are addressed by their full path in the API format.
        JObject inputs = new()
        {
            ["max_length"] = g.UserInput.Get(MaxLength, 1024),
            ["sampling_mode"] = "on",
            ["sampling_mode.temperature"] = g.UserInput.Get(Temperature, 0.7),
            ["sampling_mode.top_k"] = g.UserInput.Get(TopK, 64),
            ["sampling_mode.top_p"] = g.UserInput.Get(TopP, 0.95),
            ["sampling_mode.min_p"] = g.UserInput.Get(MinP, 0.05),
            ["sampling_mode.repetition_penalty"] = g.UserInput.Get(RepetitionPenalty, 1.05),
            ["sampling_mode.seed"] = Math.Max(seed, 0),
            ["thinking"] = g.UserInput.Get(Thinking, false),
            ["use_default_template"] = true
        };
        if (g.UserInput.Get(PromptImages, false) && PromptImageBatch(g) is JArray images)
        {
            inputs["image"] = images;
        }
        // The encoder's tokenizer swaps this in for its own system turn, keeping its native chat template (and so thinking mode and image tokens) intact.
        if (system is not null)
        {
            inputs["system_prompt"] = system;
        }
        return new(clip, inputs);
    }

    /// <summary>Loads all the prompt images as one image batch, sized the same way Swarm sizes images it feeds to text encoders. Null if there are none.</summary>
    static JArray PromptImageBatch(WorkflowGenerator g)
    {
        JArray batch = null;
        for (int i = 0; g.GetPromptImage(true, true, i) is JArray image; i++)
        {
            batch = batch is null ? image : new JArray(g.CreateNode("ImageBatch", new JObject() { ["image1"] = batch, ["image2"] = image }), 0);
        }
        return batch;
    }

    /// <summary>Creates a 'TextGenerate' node for one prompt, plus metadata nodes so the user can see what came out of it.</summary>
    static JArray Enhance(WorkflowGenerator g, Setup setup, string userPrompt, string metaKey)
    {
        JObject inputs = (JObject)setup.Inputs.DeepClone();
        inputs["clip"] = setup.Clip.DeepClone();
        inputs["prompt"] = userPrompt;
        string gen = g.CreateNode("TextGenerate", inputs);
        SaveMetadata(g, metaKey, [gen, 0]);
        if (inputs.Value<bool>("thinking"))
        {
            SaveMetadata(g, $"{metaKey}_thinking", [gen, 1]);
        }
        return [gen, 0];
    }

    static void SaveMetadata(WorkflowGenerator g, string key, JArray value)
    {
        g.CreateNode("SwarmAddSaveMetadataWS", new JObject()
        {
            ["key"] = key,
            ["value"] = value
        });
    }
}
