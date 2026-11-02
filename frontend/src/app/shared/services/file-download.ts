import { Injectable } from '@angular/core';
@Injectable({ providedIn: 'root' })
export class FileDownload {
  fromUrl(url: string): void {
    const link = document.createElement('a');
    link.href = url;
    link.download = '';
    link.click();
  }
  save(blob: Blob, name: string): void {
    const url = URL.createObjectURL(blob),
      link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}
