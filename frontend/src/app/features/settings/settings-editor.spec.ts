import { TemplateRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SettingsEditor } from './settings-editor';

describe('SettingsEditor', () => {
  it('waits for the old drawer to finish closing before showing the next editor', async () => {
    const editor = TestBed.inject(SettingsEditor);
    const content = {} as TemplateRef<unknown>;
    const cleared = vi.fn();
    await editor.open({ title: 'First', content, onClosed: cleared });
    await editor.close();
    const next = editor.open({ title: 'Second', content });
    await Promise.resolve();
    expect(editor.state()).toBeNull();
    editor.drawerClosed();
    await next;
    expect(editor.state()?.title).toBe('Second');
    expect(cleared).toHaveBeenCalledOnce();
  });
  it('does not close a busy editor or drop its one-time content', async () => {
    const editor = TestBed.inject(SettingsEditor);
    await editor.open({ title: 'Saving', content: {} as TemplateRef<unknown>, busy: () => true });
    expect(await editor.close()).toBe(false);
    expect(editor.state()?.title).toBe('Saving');
  });
});
