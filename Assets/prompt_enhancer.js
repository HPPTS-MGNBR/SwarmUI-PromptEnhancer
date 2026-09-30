/** Prompt Enhancer: popup to create, edit and delete system prompts, opened from a button next to the '[PE] System Prompt' dropdown. */
class PromptEnhancerSystemPrompts {
    constructor() {
        this.prompts = {};
        this.builtins = ['Default'];
        this.selected = null;
        this.modal = null;
        postParamBuildSteps.push(() => this.addButton());
    }

    /** Adds the edit button next to the system prompt dropdown, each time the parameter list is rebuilt. */
    addButton() {
        let select = document.getElementById('input_pesystemprompt');
        if (!select || document.getElementById('pe_sysprompt_edit_button')) {
            return;
        }
        let button = document.createElement('button');
        button.id = 'pe_sysprompt_edit_button';
        button.type = 'button';
        button.className = 'basic-button pe-sysprompt-edit-button';
        button.innerText = 'Edit';
        button.addEventListener('click', () => this.open());
        select.insertAdjacentElement('afterend', button);
    }

    /** Builds the popup on first use. */
    buildModal() {
        document.body.insertAdjacentHTML('beforeend', `
        <div class="modal" tabindex="-1" role="dialog" id="pe_sysprompt_modal">
            <div class="modal-dialog pe-sysprompt-dialog" role="document">
                <div class="modal-content">
                    <div class="modal-header"><h5 class="modal-title">Prompt Enhancer System Prompts</h5></div>
                    <div class="modal-body">
                        <div class="pe-sysprompt-toolbar">
                            <select class="auto-dropdown" id="pe_sysprompt_list"></select>
                            <button type="button" class="basic-button" id="pe_sysprompt_new">New</button>
                            <input type="text" class="auto-text" id="pe_sysprompt_name" placeholder="Name" autocomplete="off">
                        </div>
                        <textarea class="auto-text auto-text-block pe-sysprompt-text" id="pe_sysprompt_text" placeholder="System prompt..."></textarea>
                    </div>
                    <div class="modal-footer">
                        <button type="button" class="btn btn-primary basic-button" id="pe_sysprompt_save">Save</button>
                        <button type="button" class="btn btn-secondary basic-button" id="pe_sysprompt_delete">Delete</button>
                        <button type="button" class="btn btn-secondary basic-button" id="pe_sysprompt_close">Close</button>
                    </div>
                </div>
            </div>
        </div>`);
        this.modal = document.getElementById('pe_sysprompt_modal');
        this.list = document.getElementById('pe_sysprompt_list');
        this.nameInput = document.getElementById('pe_sysprompt_name');
        this.textInput = document.getElementById('pe_sysprompt_text');
        this.deleteButton = document.getElementById('pe_sysprompt_delete');
        this.list.addEventListener('change', () => this.select(this.list.value));
        document.getElementById('pe_sysprompt_new').addEventListener('click', () => this.select(null));
        document.getElementById('pe_sysprompt_save').addEventListener('click', () => this.save());
        this.deleteButton.addEventListener('click', () => this.delete());
        document.getElementById('pe_sysprompt_close').addEventListener('click', () => $(this.modal).modal('hide'));
    }

    /** Opens the popup, on whichever system prompt is currently selected in the dropdown. */
    open() {
        if (!this.modal) {
            this.buildModal();
        }
        genericRequest('PromptEnhancerListSystemPrompts', {}, data => {
            this.load(data);
            let current = document.getElementById('input_pesystemprompt')?.value;
            this.select(current in this.prompts ? current : this.builtins[0]);
            $(this.modal).modal('show');
        });
    }

    /** Takes in a server response listing the system prompts, and rebuilds the popup's list. */
    load(data) {
        this.prompts = data.prompts;
        this.builtins = data.builtins;
        // Placeholder shown while editing a new, not yet saved, system prompt.
        this.list.innerHTML = '<option value="" disabled hidden>(new)</option>';
        let names = Object.keys(this.prompts).sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()));
        for (let name of names) {
            let option = document.createElement('option');
            option.value = name;
            option.textContent = name;
            this.list.appendChild(option);
        }
    }

    /** Shows the given system prompt in the editor, or a blank one when null. */
    select(name) {
        this.selected = name;
        this.list.value = name ?? '';
        this.nameInput.value = name ?? '';
        this.textInput.value = name ? this.prompts[name] : '';
        this.deleteButton.innerText = this.builtins.includes(name) ? 'Reset' : 'Delete';
        this.deleteButton.disabled = name == null;
    }

    /** Saves the editor's content, renaming the selected system prompt if its name was changed. */
    save() {
        let name = this.nameInput.value.trim();
        if (name != this.selected && name in this.prompts && !confirm(`Overwrite the existing system prompt '${name}'?`)) {
            return;
        }
        genericRequest('PromptEnhancerSaveSystemPrompt', { name: name, text: this.textInput.value, oldName: this.selected ?? '' }, data => {
            this.load(data);
            this.select(name);
            this.syncDropdown(name);
        });
    }

    /** Deletes the selected system prompt, or resets the built-in one to its original text. */
    delete() {
        let name = this.selected;
        if (name == null || (!this.builtins.includes(name) && !confirm(`Delete the system prompt '${name}'?`))) {
            return;
        }
        genericRequest('PromptEnhancerDeleteSystemPrompt', { name: name }, data => {
            this.load(data);
            this.select(name in this.prompts ? name : this.builtins[0]);
            this.syncDropdown(this.selected);
        });
    }

    /** Refreshes the parameter dropdown's values, and selects the given system prompt in it. */
    syncDropdown(name) {
        refreshParameterValues(false, null, () => {
            let dropdown = document.getElementById('input_pesystemprompt');
            if (dropdown && name in this.prompts) {
                dropdown.value = name;
                triggerChangeFor(dropdown);
            }
        });
    }
}

promptEnhancerSystemPrompts = new PromptEnhancerSystemPrompts();

/** Prompt Enhancer: button at the top-left of the prompt box, that enhances the prompt in place without generating an image.
 * (Magic Prompt's buttons sit centered on the same edge, and the token count on the right.) */
class PromptEnhancerButton {
    constructor() {
        this.label = '✨ PE Enhance';
        this.box = getRequiredElementById('alt_prompt_textbox');
        this.button = document.createElement('button');
        this.button.type = 'button';
        this.button.className = 'basic-button pe-enhance-button';
        this.button.innerText = this.label;
        this.button.title = 'Prompt Enhancer: rewrite the prompt with the text encoder, using the Prompt Enhancer settings.\nOnly the text before the first <tag> is rewritten.';
        this.button.addEventListener('click', () => this.enhance());
        getRequiredElementById('alt_prompt_region').appendChild(this.button);
    }

    /** Sends the current generate tab parameters to be enhanced, and replaces the prompt with the result. */
    enhance() {
        if (!this.box.value.trim() || this.button.disabled) {
            return;
        }
        this.setBusy(true);
        genericRequest('PromptEnhancerEnhancePrompt', getGenInput(), data => {
            this.setBusy(false);
            this.box.value = data.prompt;
            triggerChangeFor(this.box);
        }, 0, e => {
            this.setBusy(false);
            showError(e);
        });
    }

    /** Describes the given image with the text encoder, and replaces the prompt with the result. */
    caption(src) {
        if (this.button.disabled) {
            return;
        }
        this.setBusy(true, '✨ Captioning...');
        let send = (data) => {
            genericRequest('PromptEnhancerCaptionImage', getGenInput({ pe_caption_image: data }), result => {
                this.setBusy(false);
                this.box.value = result.prompt;
                triggerChangeFor(this.box);
            }, 0, e => {
                this.setBusy(false);
                showError(e);
            });
        };
        if (src.startsWith('data:')) {
            send(src);
        }
        else {
            toDataURL(src, send);
        }
    }

    /** Locks the button while a request is running. */
    setBusy(busy, text = '✨ Enhancing...') {
        this.button.disabled = busy;
        this.button.innerText = busy ? text : this.label;
    }
}

promptEnhancerButton = new PromptEnhancerButton();

// In the 'More' dropdown, not in the visible button row. Also shown in the history panel: extensions that wrap 'buttonsForImage' (eg Base2Edit) can drop its 'isCurrentImage' argument, which would hide a current-image-only button everywhere.
registerMediaButton('PE Caption', src => promptEnhancerButton.caption(src), 'Prompt Enhancer: describe this image with the text encoder, and replace the prompt with the description.\nUses the built-in \'Caption\' system prompt, unless you picked another one than \'Default\'.', ['image']);
