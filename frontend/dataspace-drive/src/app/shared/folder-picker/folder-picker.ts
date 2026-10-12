import { Component, inject, input, output } from '@angular/core';
import { DxTreeViewModule } from 'devextreme-angular/ui/tree-view';
import { ItemSelectionChangedEvent, Node } from 'devextreme/ui/tree_view';

import { DriveApiService } from '../../core/drive-api.service';

export interface FolderNode {
  id: string;
  text: string;
  parentId: string | null;
  hasItems: boolean;
  path: string;
}

/** Ordnerbaum zum Auswählen eines Ziels: beschreibbare Bereiche, Unterordner werden beim Aufklappen geladen. */
@Component({
  selector: 'app-folder-picker',
  imports: [DxTreeViewModule],
  templateUrl: './folder-picker.html',
  styleUrl: './folder-picker.scss',
})
export class FolderPicker {
  private readonly drive = inject(DriveApiService);

  readonly height = input(320);
  readonly selected = output<FolderNode | null>();

  createChildren = async (parent: Node<FolderNode> | null): Promise<FolderNode[]> => {
    if (!parent) {
      const spaces = await this.drive.spaces();
      return spaces
        .filter((s) => s.permission === 'Write' || s.permission === 'Manage')
        .map((s) => ({ id: s.rootFolderId, text: s.name, parentId: null, hasItems: true, path: s.name }));
    }
    const node = parent.itemData as FolderNode;
    const listing = await this.drive.children(node.id);
    return listing.items
      .filter((i) => i.isDirectory)
      .map((i) => ({ id: i.id, text: i.name, parentId: node.id, hasItems: true, path: `${node.path} / ${i.name}` }));
  };

  onSelectionChanged(e: ItemSelectionChangedEvent<FolderNode>): void {
    this.selected.emit(e.node?.selected ? (e.itemData ?? null) : null);
  }
}
