import { inject, Injectable } from '@angular/core';
import { Clipboard } from '@angular/cdk/clipboard';

@Injectable({ providedIn: 'root' })
export class ClipboardService {
  private readonly cdkClipboard = inject(Clipboard);

  async copy(text: string): Promise<boolean> {
    if (!text) {
      return false;
    }

    // 1. Essai avec l'API asynchrone moderne du presse-papier si le contexte est sécurisé (HTTPS / localhost sécurisé)
    if (
      typeof navigator !== 'undefined' &&
      navigator.clipboard &&
      typeof window !== 'undefined' &&
      window.isSecureContext
    ) {
      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch {
        // Poursuite vers le repli CDK si l'API asynchrone échoue (refus de permission, focus perdu, etc.)
      }
    }

    // 2. Repli Angular CDK (création d'un textarea hors écran + execCommand)
    try {
      if (this.cdkClipboard.copy(text)) {
        return true;
      }
    } catch {
      // Poursuite vers le repli DOM direct
    }

    // 3. Repli DOM direct pour contextes restreints / iframes
    if (typeof document !== 'undefined') {
      try {
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.top = '0';
        textarea.style.left = '-9999px';
        textarea.style.opacity = '0';
        textarea.setAttribute('readonly', '');
        document.body.appendChild(textarea);
        textarea.select();
        textarea.setSelectionRange(0, text.length);
        const successful = document.execCommand('copy');
        document.body.removeChild(textarea);
        return successful;
      } catch {
        return false;
      }
    }

    return false;
  }
}
